using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// A nonreusable eligibility container for one successful allocation. Serializes all operations; only the
/// lobby may issue or revoke grants for an exact session. A trusted transport adapter must retain its own
/// allocation instance and supply a server-verified IPv4 relay endpoint belonging to that connection.
/// Deliver secrets only over authenticated encrypted channels, never public UDP. This container does not
/// verify public endpoint ownership or per-packet integrity. Production CONNECT calls must also pass the
/// exact session owned by the current TLS stream.
/// </summary>
public sealed class MatchAdmissionRound
{
    /// <summary>
    /// Unredeemed secrets expire after 30 seconds as an implementation limit, not a rule of the original DS
    /// protocol.
    /// </summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<GameSession, Membership> members;
    private readonly Dictionary<Guid, Membership> pending = [];
    private bool revoked;
    private bool active;

    internal MatchAdmissionRound(GameRoom room, TimeProvider clock)
    {
        this.clock = clock;
        RoomId = room.RoomId;
        AllocationId = Guid.NewGuid();
        active = true;
        members = room.Members.ToDictionary(member => member.Session,
            member => new Membership(member.Session, member.SeatIndex, member.Observer));
    }

    internal MatchAdmissionRound(MatchStartSnapshot snapshot, TimeProvider clock)
    {
        this.clock = clock;
        RoomId = snapshot.RoomId;
        AllocationId = snapshot.AllocationId;
        members = snapshot.Members.ToDictionary(member => member.Session,
            member => new Membership(member.Session, member.SeatIndex, member.Observer));
    }

    /// <summary>The owning lobby room. Its next round still requires a new instance and ID.</summary>
    public ulong RoomId { get; }

    /// <summary>
    /// This allocation's ID alone cannot authenticate a caller or restore access to an old round.
    /// </summary>
    public Guid AllocationId { get; }

    /// <summary>
    /// Redemption opens only after the lobby's final start-version check. Secrets issued during preparation
    /// cannot enter the native world early.
    /// </summary>
    internal void Activate()
    {
        lock (gate)
        {
            if (revoked) throw new InvalidOperationException("已撤销的回合不能重新激活。");

            active = true;
        }
    }

    internal MatchAdmissionTicket Issue(GameSession session)
    {
        lock (gate)
        {
            if (revoked || session.IsDisconnected || !members.TryGetValue(session, out var member) ||
                member.Revoked || member.Lease is not null)
                throw new InvalidOperationException("本会话没有可签发的当前回合资格。");

            // Reissuing replaces an unredeemed secret. Redeemed, departed, or revoked eligibility cannot be recreated in the same round.
            ClearPending(member);

            byte[] secret = RandomNumberGenerator.GetBytes(32);

            member.AdmissionId = Guid.NewGuid();
            member.SecretHash = SHA256.HashData(secret);
            member.IssuedAt = clock.GetTimestamp();
            pending.Add(member.AdmissionId, member);
            return new MatchAdmissionTicket(AllocationId, member.AdmissionId, secret);
        }
    }

    /// <summary>
    /// Redeems an independent secret with a source endpoint already verified by a trusted adapter. Invalid
    /// input consumes no other grant; each grant succeeds only once, and an endpoint cannot belong to two
    /// members in one round. Supports IPv4 only, rejecting client-reported endpoints and shared DS keys.
    /// This is not a public UDP parser; future adapters must establish endpoint ownership through the
    /// authenticated channel first.
    /// </summary>
    public bool TryRedeem(Guid admissionId, ReadOnlySpan<byte> secret, IPEndPoint observedEndpoint,
        out MatchAdmissionLease? lease)
        => TryRedeemCore(null, admissionId, secret, observedEndpoint, out lease);

    /// <summary>
    /// Production redemption entry point: the secret, endpoint, and grant must belong to the same
    /// authenticated session that sent this control frame.
    /// </summary>
    public bool TryRedeem(GameSession session, Guid admissionId, ReadOnlySpan<byte> secret,
        IPEndPoint observedEndpoint, out MatchAdmissionLease? lease)
    {
        ArgumentNullException.ThrowIfNull(session);

        return TryRedeemCore(session, admissionId, secret, observedEndpoint, out lease);
    }

    private bool TryRedeemCore(GameSession? session, Guid admissionId, ReadOnlySpan<byte> secret,
        IPEndPoint observedEndpoint, out MatchAdmissionLease? lease)
    {
        lease = null;
        if (secret.Length != 32 || !TryCopyEndpoint(observedEndpoint, out var endpoint)) return false;

        Span<byte> digest = stackalloc byte[32];

        SHA256.HashData(secret, digest);

        lock (gate)
        {
            if (!active || revoked || !pending.TryGetValue(admissionId, out var member) || member.Revoked ||
                member.Session.IsDisconnected || member.SecretHash is null ||
                (session is not null && !ReferenceEquals(member.Session, session))) return false;

            TimeSpan elapsed = clock.GetElapsedTime(member.IssuedAt);

            if (elapsed < TimeSpan.Zero || elapsed >= TicketLifetime)
            {
                ClearPending(member);
                return false;
            }
            if (!CryptographicOperations.FixedTimeEquals(digest, member.SecretHash) ||
                members.Values.Any(other => other.Lease is { } existing && existing.Matches(endpoint!))) return false;

            lease = new MatchAdmissionLease(AllocationId, member.Session, member.SeatIndex, member.Observer, endpoint!);
            member.Lease = lease;
            ClearPending(member);
            return true;
        }
    }

    /// <summary>
    /// Checks instance provenance, revocation, and the current datagram endpoint. The result applies only
    /// at the instant of the check and cannot be cached as lasting authorization. Endpoint migration is
    /// unsupported; occupied endpoints remain reserved until round end to keep late datagrams from reaching
    /// another member.
    /// </summary>
    public bool IsCurrent(MatchAdmissionLease lease, IPEndPoint observedEndpoint)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!TryCopyEndpoint(observedEndpoint, out var endpoint)) return false;
        lock (gate)
        {
            return active && !revoked && !lease.IsRevoked && lease.Matches(endpoint!) &&
                members.Values.Any(member => ReferenceEquals(member.Lease, lease));
        }
    }

    /// <summary>
    /// Serializes synchronous datagram submission with revocation. submit must be bounded and must not wait
    /// for lobby locks or asynchronous callbacks.
    /// </summary>
    public bool TryUseLease(MatchAdmissionLease lease, IPEndPoint observedEndpoint, Action submit)
    {
        ArgumentNullException.ThrowIfNull(submit);

        lock (gate)
        {
            if (!IsCurrent(lease, observedEndpoint)) return false;
            submit();

            return true;
        }
    }

    internal void Revoke(GameSession session)
    {
        lock (gate)
        {
            if (!members.TryGetValue(session, out var member)) return;
            member.Revoked = true;
            ClearPending(member);
            member.Lease?.Revoke();
        }
    }

    internal void RevokeAll()
    {
        lock (gate)
        {
            revoked = true;
            foreach (var member in members.Values)
            {
                member.Revoked = true;
                ClearPending(member);
                member.Lease?.Revoke();
            }
        }
    }

    private void ClearPending(Membership member)
    {
        pending.Remove(member.AdmissionId);
        if (member.SecretHash is null) return;
        CryptographicOperations.ZeroMemory(member.SecretHash);
        member.SecretHash = null;
    }

    private static bool TryCopyEndpoint(IPEndPoint? endpoint, out IPEndPoint? copy)
    {
        copy = null;
        if (endpoint is null) return false;

        int port = endpoint.Port;
        IPAddress sourceAddress = endpoint.Address;

        if (port is < 1 or > 65535 || sourceAddress.AddressFamily != AddressFamily.InterNetwork) return false;

        byte[] address = sourceAddress.GetAddressBytes();

        if (address[0] is 0 or >= 224) return false;
        copy = new IPEndPoint(new IPAddress(address), port);
        return true;
    }

    private sealed class Membership(GameSession session, ushort seatIndex, bool observer)
    {
        internal GameSession Session { get; } = session;
        internal ushort SeatIndex { get; } = seatIndex;
        internal bool Observer { get; } = observer;
        internal Guid AdmissionId { get; set; }
        internal byte[]? SecretHash { get; set; }
        internal long IssuedAt { get; set; }
        internal bool Revoked { get; set; }
        internal MatchAdmissionLease? Lease { get; set; }
    }
}
