namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Encirclement retains TGLDGame's four-player limit. Maps lobby Normal/Hard/Elite to three native tiers;
/// empty means Normal.
/// </summary>
internal static class AfdevEncirclementSettings
{
    internal const int MaximumPlayers = 4;

    internal static byte Difficulty(uint subModeId, uint roomFlags = 0) => AfdevPveDifficulty.Resolve(subModeId, 0, roomFlags);
}
