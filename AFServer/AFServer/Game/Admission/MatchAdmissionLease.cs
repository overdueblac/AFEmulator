using System.Net;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Associates a session and endpoint after one-time redemption. Creates no Pawn and grants no command
/// access to an existing simulator. Adapters must check current eligibility when committing a command and
/// synchronize revocation with the simulator; caching a successful check cannot extend eligibility.
/// </summary>
public sealed class MatchAdmissionLease
{
    private readonly GameSession session;
    private readonly IPEndPoint endpoint;
    private int revoked;

    internal MatchAdmissionLease(Guid allocationId, GameSession session, ushort seatIndex, bool observer,
        IPEndPoint endpoint)
    {
        AllocationId = allocationId;
        this.session = session;
        SeatIndex = seatIndex;
        Observer = observer;
        this.endpoint = endpoint;
    }

    /// <summary>The unique round-allocation ID that created this grant.</summary>
    public Guid AllocationId { get; }

    /// <summary>
    /// The account from the exact authenticated session, used only to index profile data, never to recover
    /// eligibility.
    /// </summary>
    public ulong Uin => session.Uin;

    /// <summary>The seat in this round's snapshot.</summary>
    public ushort SeatIndex { get; }

    /// <summary>Observer eligibility cannot be used as combat-player eligibility.</summary>
    public bool Observer { get; }

    /// <summary>
    /// Returns a copy of the actual receive endpoint; external mutation cannot migrate eligibility.
    /// </summary>
    public IPEndPoint RemoteEndpoint => new(new IPAddress(endpoint.Address.GetAddressBytes()), endpoint.Port);

    /// <summary>
    /// Revocation is irreversible. Returns true immediately on disconnect even if room cleanup is canceled.
    /// </summary>
    public bool IsRevoked => Volatile.Read(ref revoked) != 0 || session.IsDisconnected;

    internal bool Matches(IPEndPoint candidate) => endpoint.Equals(candidate);

    internal void Revoke() => Interlocked.Exchange(ref revoked, 1);
}
