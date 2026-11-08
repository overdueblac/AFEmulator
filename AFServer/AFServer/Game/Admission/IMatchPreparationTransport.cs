using AFConnect.Proxy;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Reports preparation progress for an authenticated session. The operation ID grants no eligibility, and
/// cancellation affects only the registered snapshot.
/// </summary>
public interface IMatchPreparationTransport
{
    /// <summary>
    /// Sends states in order for the same operation. A terminal state removes cancellation access; delivery
    /// failure must prevent match publication.
    /// </summary>
    ValueTask ReportPreparationAsync(Guid operationId, AfdevPreparationStage stage, ushort attempt,
        ushort maximumAttempts, ushort remainingSeconds, Action cancel, CancellationToken cancellationToken);
}
