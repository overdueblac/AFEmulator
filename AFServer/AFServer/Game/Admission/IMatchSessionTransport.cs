using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// A trusted CONNECT adapter bound to an authenticated ZONE session. The lobby invokes it outside its lock;
/// never find a replacement connection by UIN.
/// </summary>
public interface IMatchSessionTransport
{
    /// <summary>
    /// Delivers eligibility over the same TLS stream and waits for the local listener, returning a local
    /// client endpoint in the original A11A layout.
    /// </summary>
    ValueTask<MatchEndpoint> PrepareAsync(MatchStartSnapshot snapshot, MatchAdmissionRound round,
        MatchAdmissionTicket ticket, MatchEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Eligibility has already been revoked. Stops the exact route and relay for this allocation before
    /// returning, leaving the next round untouched.
    /// </summary>
    ValueTask RevokeAsync(Guid allocationId, CancellationToken cancellationToken);
}
