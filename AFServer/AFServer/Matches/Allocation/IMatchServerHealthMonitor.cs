namespace AFServer.AssaultFire.Game;

/// <summary>
/// Match-process health checks allow the business layer to release an old round after deferred startup
/// failure and permit a new start.
/// </summary>
public interface IMatchServerHealthMonitor
{
    /// <summary>
    /// Returns true when the allocation is lost, the bridge has exited, or the proxy explicitly reports
    /// failure. Never reallocates a process.
    /// </summary>
    ValueTask<bool> HasFailedAsync(ulong roomId, CancellationToken cancellationToken);
}
