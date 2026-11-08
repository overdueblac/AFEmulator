using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Reads settlement from this allocation's native world before cleanup. A new session or room ID cannot
/// replace the frozen start identity.
/// </summary>
public interface IMatchSettlementAllocator
{
    ValueTask<MatchSettlementSnapshot> ReadSettlementAsync(MatchStartSnapshot snapshot,
        CancellationToken cancellationToken);
}
