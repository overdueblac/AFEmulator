using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Consumes the unique start object frozen under the lobby lock. Never recaptures the room or creates
/// another allocation after slow startup.
/// </summary>
public interface IMatchSnapshotAllocator : IMatchServerAllocator
{
    ValueTask<MatchEndpoint> ReserveAsync(MatchStartSnapshot snapshot, CancellationToken cancellationToken);
}
