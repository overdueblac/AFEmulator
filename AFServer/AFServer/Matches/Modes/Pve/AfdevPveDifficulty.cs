namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Maps four lobby difficulties to this build's three multiplayer PVE tiers. Easy belongs only to the
/// native single-player branch.
/// </summary>
internal static class AfdevPveDifficulty
{
    private const uint AdvancedHeroFlag = 0x40000;

    internal static byte Resolve(uint subModeId, byte defaultDifficulty, uint roomFlags = 0)
    {
        // Client-added Elite folds back to 0x1003 at commit and is distinguished by RF_ADVANCEDHERO_ENABLED.
        if ((roomFlags & AdvancedHeroFlag) != 0)
        {
            if (subModeId is 0x1003 or 0x1004) return 2;

            throw new InvalidOperationException("PVE 精英标志必须对应困难基础编号或显式精英编号。");
        }

        return subModeId switch
        {
            0 => defaultDifficulty,
            >= 0x1002 and <= 0x1004 => (byte)(subModeId - 0x1002),
            0x1001 => throw new InvalidOperationException("当前 PVE 地图不支持联机简单难度，请选择普通、困难或精英。"),
            _ => throw new InvalidOperationException("PVE 难度子模式只能为空或 0x1002–0x1004（普通、困难、精英）。")
        };
    }

    // Native Easy/Normal/Hard display as Normal/Hard/Elite. Standalone is not a fourth multiplayer tier.
    internal static uint NativeSubMode(byte difficulty) => difficulty <= 2
        ? 0x1001u + difficulty
        : throw new InvalidOperationException("原生 PVE 联机难度必须为 0–2。");
}
