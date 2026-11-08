using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

internal sealed partial class DefenseSettlementStatistics
{
    /// <summary>
    /// Starship Invasion report from native 0x01475830, preserving mech order, traps, and Boss weak-point
    /// fields.
    /// </summary>
    internal static DefenseSettlementStatistics ReadStarshipPlayer(ulong uin, float worldTime, ushort gameTime,
        ushort round, byte[] pri, PveSettlementArrays arrays)
    {
        if (pri.Length < 0x1000) throw new InvalidDataException("星海虫影 PRI 统计源尺寸不足。");

        byte[] common = ReadCommonPlayer(uin, worldTime, gameTime, round, pri, arrays);
        byte[] extension = new byte[118];
        int[] mechaOffsets = [0xFA4, 0xFCC, 0xFB8, 0xFE0];

        // Native code reorders four mech types as 0,2,1,3. GiveUpCount is not the report's death count.
        for (int i = 0; i < mechaOffsets.Length; i++)
        {
            int source = mechaOffsets[i];
            int target = i * 16;

            Put32(extension, target, U32(pri, source));
            Put32(extension, target + 4, U32(pri, source + 12));
            Put32(extension, target + 8, U32(pri, source + 8));
            Put32(extension, target + 12, U32(pri, source + 16));
        }

        pri.AsSpan(0xF74, 48).CopyTo(extension.AsSpan(64));
        Put32(extension, 112, U32(pri, 0xF70));
        PveSettlementStatistics.Put16(extension, 116, PveSettlementStatistics.U16(pri, 0xFFC));
        Put32(common, 0x442, U32(pri, 0xFF4));
        PveSettlementStatistics.Put16(common, 0x446, PveSettlementStatistics.U16(pri, 0xFF8));
        PveSettlementStatistics.Put16(common, 0x448, PveSettlementStatistics.U16(pri, 0xFFC));

        // Native IF2 leaves TimesSpecKill/TimesSpecKillScore unset. Do not apply legacy IF supplemental fields.
        return new(common, (U32(pri, 0x3C0) & (1u << 22)) != 0, extension);
    }

    internal static TdrWriter WriteStarshipGame(byte[] common, byte[] gri, float worldTime,
        IReadOnlyList<DefenseSettlementStatistics> players)
    {
        if (common.Length != 0xD0 || gri.Length < 0x4E4 || gri[0x31C] > 2 || I32(gri, 0x4B8) < 0 || players.Count > 8)
            throw new InvalidDataException("星海虫影难度、阶段或真人统计数量超出已核验范围。");

        int round = I32(gri, 0x4B8);
        double stageTime = (double)worldTime + (round <= 4 ? 0 : round <= 9 ? 600 : 1500) - Single(gri, 0x4BC);
        var writer = new TdrWriter();

        // Truncate to integer only after native x87 arithmetic; intermediate float rounding could raise a fractional second to the next integer.
        if (!double.IsFinite(stageTime) || stageTime < 0 || stageTime >= int.MaxValue)
            throw new InvalidDataException("星海虫影原生阶段时间无效。");

        common[7] = checked((byte)(gri[0x31C] + 1));
        PveSettlementStatistics.Put16(common, 0xC, PveSettlementStatistics.U16(common, 0xC) +
            players.Count(player => player.mechaLicense));
        PveSettlementStatistics.Put16(common, 0x2E, unchecked((ushort)(int)stageTime));
        common[0x44] = gri[0x4C0];
        common[0x45] = gri[0x4C4];
        common[0x46] = gri[0x4C8];
        common[0x48] = TruncateBaseHealth(Single(gri, 0x4CC));
        common[0x49] = TruncateBaseHealth(Single(gri, 0x4D0));
        common[0x3B] = gri[0x4D4];
        PveSettlementStatistics.Put16(common, 0x40, PveSettlementStatistics.U16(gri, 0x4DC));
        common[0x3E] = gri[0x4E0];

        PveSettlementStatistics.WriteGame(writer, common);
        writer.U32(0).U32(0); // Same-version IF2 leaves whole-match mech/trap-user extensions unset.

        return writer;
    }

    private static byte TruncateBaseHealth(float value)
    {
        if (!float.IsFinite(value) || value < 0 || value >= int.MaxValue)
            throw new InvalidDataException("星海虫影原生基地血量统计无效。");

        // 0x0147595F/0x0147596A truncate floats directly without multiplying by 100. Preserve native client compatibility.
        return unchecked((byte)(int)value);
    }
}
