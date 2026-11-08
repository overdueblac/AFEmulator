namespace AFServer.AssaultFire.Game;

/// <summary>
/// Independent match-process allocation interface. Failures must throw; never publish unusable endpoints.
/// </summary>
public interface IMatchServerAllocator
{
    ValueTask<MatchEndpoint> ReserveAsync(GameRoom room, CancellationToken cancellationToken);
    ValueTask ReleaseAsync(ulong roomId, CancellationToken cancellationToken);
}
