using System.Buffers.Binary;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Supplemental Defense reports. IF and IF2 use their own native layouts with shared fields, fixed
/// extensions, and leaderboard wire format.
/// </summary>
internal sealed partial class DefenseSettlementStatistics(byte[] common, bool hasMechaLicense, byte[]? extension = null)
{
    private static readonly (int Target, int Source, int Size)[] PlayerFields =
    [
        (0x41B, 0xF24, 2), // IFStatis.TimesKillFly
        (0x41D, 0xF28, 2), // IFStatis.TimesKillZA5
        (0x41F, 0xF30, 2), // IFStatis.TimesKillMainArtillery
        (0x421, 0xF34, 2), // IFStatis.TimesKillMachinegun
        (0x423, 0xF3C, 2), // IFStatis.TimesCallClusterMortar
        (0x425, 0xF20, 2), // IFStatis.TimesKillThrownWeapon
        (0x427, 0xF2C, 2), // IFStatis.TimesKillHeadShot
        (0x42A, 0xF38, 2), // IFStatis.TimesKillMecha
        (0x42C, 0xF40, 2), // IFStatis.MechaKillScore
    ];

    private readonly bool mechaLicense = hasMechaLicense;

    internal static DefenseSettlementStatistics ReadPlayer(ulong uin, float worldTime, ushort gameTime,
        ushort round, byte[] pri, PveSettlementArrays arrays)
    {
        if (pri.Length < 0xF84) throw new InvalidDataException("钢铁森林 PRI 统计源尺寸不足。");

        byte[] common = ReadCommonPlayer(uin, worldTime, gameTime, round, pri, arrays);

        PveSettlementStatistics.Put16(common, 0x020, PveSettlementStatistics.U16(pri, 0xEAC));
        PveSettlementStatistics.Put16(common, 0x022, PveSettlementStatistics.U16(pri, 0xEB0));
        Put32(common, 0x442, U32(pri, 0xF78));
        PveSettlementStatistics.Put16(common, 0x446, PveSettlementStatistics.U16(pri, 0xF7C));
        PveSettlementStatistics.Put16(common, 0x448, PveSettlementStatistics.U16(pri, 0xF80));

        return new(common, (U32(pri, 0x3C0) & (1u << 22)) != 0);
    }

    private static byte[] ReadCommonPlayer(ulong uin, float worldTime, ushort gameTime,
        ushort round, byte[] pri, PveSettlementArrays arrays)
    {
        byte[] common = PveSettlementStatistics.ReadPlayer(0x2002, uin, worldTime, gameTime, pri, arrays);
        uint flags = U32(pri, 0xE88);

        PveSettlementStatistics.Put16(common, 8, round);

        foreach (var field in PlayerFields)
            pri.AsSpan(field.Source, field.Size).CopyTo(common.AsSpan(field.Target));

        common[0x429] = (byte)((flags >> 2) & 1);
        common[0x42E] = (byte)(1 - ((flags >> 4) & 1));
        common[0x42F] = (byte)((flags >> 3) & 1);

        return common;
    }

    internal void WriteTo(TdrWriter writer)
    {
        PveSettlementStatistics.WritePlayer(writer, common);

        if (extension is not null)
        {
            // PVEPlayerStatisIF has 29 four-byte fields plus a two-byte weak-point count. Convert host data to TDR byte order.
            for (int offset = 0; offset < 116; offset += 4) writer.U32(U32(extension, offset));

            writer.U16(PveSettlementStatistics.U16(extension, 116));

            return;
        }

        // The same-version legacy IF report leaves later PVEPlayerStatisIF extensions unset; do not fill them from ATD traps or IF2 data.
        for (int i = 0; i < 4; i++) writer.U32(0).U32(0).U32(0).U32(0); // MechaStat
        for (int i = 0; i < 4; i++) writer.U32(0).U32(0).U32(0); // TrapStat

        writer.U32(0).U16(0);
    }

    internal static TdrWriter WriteGame(byte[] common, byte[] gri, IReadOnlyList<DefenseSettlementStatistics> players)
    {
        if (common.Length != 0xD0 || gri.Length < 0x578 || gri[0x31C] > 2 || I32(gri, 0x4CC) < 0 || players.Count > 6)
            throw new InvalidDataException("钢铁森林难度、阶段或真人统计数量超出已核验范围。");

        var writer = new TdrWriter();
        int round = I32(gri, 0x4CC);
        float stageTime = Single(gri, 0x4D4) + (round <= 1 ? 0 : round == 2 ? 600 : 1500);
        uint flags = U32(gri, 0x4BC);

        common[7] = checked((byte)(gri[0x31C] + 1));
        PveSettlementStatistics.Put16(common, 0xC, PveSettlementStatistics.U16(common, 0xC) +
            players.Count(player => player.mechaLicense));
        PveSettlementStatistics.Put16(common, 0x2E, PveSettlementStatistics.Elapsed(stageTime));
        common[0x30] = (byte)((flags >> 3) & 1);
        common[0x31] = (byte)(1 - ((flags >> 5) & 1));
        common[0x32] = (byte)((flags >> 4) & 1);
        common[0x3B] = gri[0x548];
        common[0x3C] = gri[0x54C];
        common[0x3D] = gri[0x550];
        common[0x3E] = Percent(Single(gri, 0x554));
        common[0x3F] = Percent(Single(gri, 0x558));
        PveSettlementStatistics.Put16(common, 0x40, PveSettlementStatistics.U16(gri, 0x55C));
        PveSettlementStatistics.Put16(common, 0x42, PveSettlementStatistics.U16(gri, 0x560));
        common[0x44] = gri[0x564];
        common[0x45] = gri[0x568];
        common[0x46] = gri[0x56C];
        common[0x47] = Percent(Single(gri, 0x570));
        common[0x48] = gri[0x544]; // The native report takes FirstBaseLowestHealth's low byte directly.
        common[0x49] = Percent(Single(gri, 0x574));

        PveSettlementStatistics.WriteGame(writer, common);
        writer.U32(0).U32(0); // The same-version legacy IF report leaves PVEGameStatisIF extensions unset.

        return writer;
    }

    internal static void WriteEmptyRankings(TdrWriter writer, ushort mapId)
    {
        // No authoritative historical leaderboard store exists. Keep fixed identity slots empty; current-match statistics are not historical rankings.
        writer.U32(0x2002).U32(mapId);

        for (int level = 1; level <= 3; level++)
        {
            writer.U8(level);

            for (int rank = 0; rank < 10; rank++) writer.U64(0).String("", 32).U32(0).U32(0).U32(0);
        }
    }

    private static byte Percent(float fraction)
    {
        float percent = fraction * 100;

        if (!float.IsFinite(percent) || percent < 0 || percent >= int.MaxValue)
            throw new InvalidDataException("钢铁森林原生血量比例无效。");

        return unchecked((byte)(int)percent);
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static void Put32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static float Single(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));
}
