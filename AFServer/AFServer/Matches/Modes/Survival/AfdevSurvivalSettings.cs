namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Altar and Assembly Center retain TGSVGame's four-player limit. Map lobby Normal/Hard/Elite to native
/// tiers; empty means Normal.
/// </summary>
internal static class AfdevSurvivalSettings
{
    internal const int MaximumPlayers = 4;

    internal static byte Difficulty(uint subModeId, uint roomFlags = 0) => AfdevPveDifficulty.Resolve(subModeId, 0, roomFlags);
}
