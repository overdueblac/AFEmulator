namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Maps lobby Normal/Hard/Elite to ATDGameInfo's three tiers. Empty retains this build's Elite Tower
/// Defense default.
/// </summary>
internal static class AfdevTowerDefenseSettings
{
    internal static byte Difficulty(uint subModeId, uint roomFlags = 0) => AfdevPveDifficulty.Resolve(subModeId, 2, roomFlags);
}
