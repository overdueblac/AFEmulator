namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Maps lobby Normal/Hard/Elite to three native tiers for both Defense maps. Empty means Normal; actual
/// rules determine player limits.
/// </summary>
internal static class AfdevDefenseSettings
{
    internal static int MaximumPlayers(ushort mapId) => mapId switch
    {
        0x0010 => 6, // Iron Forest's TGIFGame overrides capacity to six players.
        0x005F => 8, // Starship Invasion's TGIF2Game inherits PVEGame's eight-player limit.
        _ => throw new InvalidOperationException("保卫战地图没有已核验的原生人数上限。")
    };

    internal static byte Difficulty(uint subModeId, uint roomFlags = 0) => AfdevPveDifficulty.Resolve(subModeId, 0, roomFlags);
}
