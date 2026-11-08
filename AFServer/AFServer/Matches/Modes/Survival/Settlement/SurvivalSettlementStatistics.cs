using System.Buffers.Binary;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Supplemental Survival statistics from same-version 0x01474450. SV has no IF/ATD extension union.
/// </summary>
internal sealed class SurvivalSettlementStatistics(byte[] common, ushort firstWindowDestroyedRound)
{
    internal const int MaximumRounds = 25;

    private readonly ushort windowDestroyedRound = firstWindowDestroyedRound;

    internal static SurvivalSettlementStatistics ReadPlayer(ulong uin, float worldTime, ushort gameTime,
        ushort round, byte[] pri, PveSettlementArrays arrays)
    {
        if (pri.Length < 0xFB4 || round > MaximumRounds)
            throw new InvalidDataException("生存战 PRI 统计源尺寸或波次超出已核验范围。");

        byte[] common = PveSettlementStatistics.ReadPlayer(0x2001, uin, worldTime, gameTime, pri, arrays);

        PveSettlementStatistics.Put16(common, 0x008, round);
        PveSettlementStatistics.Put16(common, 0x020, PveSettlementStatistics.U16(pri, 0xEAC));
        PveSettlementStatistics.Put16(common, 0x022, PveSettlementStatistics.U16(pri, 0xEB0));
        PveSettlementStatistics.Put16(common, 0x405, PveSettlementStatistics.U16(pri, 0xFA4));
        PveSettlementStatistics.Put16(common, 0x407, PveSettlementStatistics.U16(pri, 0xF98));
        Put32(common, 0x44A, U32(pri, 0xFA8));
        PveSettlementStatistics.Put16(common, 0x44E, PveSettlementStatistics.U16(pri, 0xFAC));
        PveSettlementStatistics.Put16(common, 0x450, PveSettlementStatistics.U16(pri, 0xFB0));

        return new(common, PveSettlementStatistics.U16(pri, 0xFA0));
    }

    internal void WriteTo(TdrWriter writer) => PveSettlementStatistics.WritePlayer(writer, common);

    internal static TdrWriter WriteGame(byte[] common, byte[] gri, byte[] jerricanRounds, byte[] autoGunRounds,
        IReadOnlyList<SurvivalSettlementStatistics> players)
    {
        if (common.Length != 0xD0 || gri.Length < 0x530 || gri[0x31C] > 2 ||
            players.Count > AfdevSurvivalSettings.MaximumPlayers)
            throw new InvalidDataException("生存战难度或真人统计数量超出已核验范围。");

        int round = BinaryPrimitives.ReadInt32LittleEndian(gri.AsSpan(0x4B8));
        float bossMaximumHealth = BinaryPrimitives.ReadSingleLittleEndian(gri.AsSpan(0x4EC));

        if (round is < 0 or > MaximumRounds || jerricanRounds.Length % 4 != 0 || autoGunRounds.Length % 4 != 0 ||
            jerricanRounds.Length / 4 < round || autoGunRounds.Length / 4 < round ||
            jerricanRounds.Length / 4 > MaximumRounds || autoGunRounds.Length / 4 > MaximumRounds)
            throw new InvalidDataException("生存战逐波统计数组必须覆盖当前波次，且不能超过客户端 25 波上限。");
        if (!float.IsFinite(bossMaximumHealth) || bossMaximumHealth < 0 || bossMaximumHealth >= int.MaxValue)
            throw new InvalidDataException("生存战原生 Boss 最大血量无效。");

        common[7] = checked((byte)(gri[0x31C] + 1));
        common[0x4B] = (byte)round;
        PveSettlementStatistics.Put16(common, 0x004, players.Count);
        PveSettlementStatistics.Put16(common, 0x008,
            players.FirstOrDefault(player => player.windowDestroyedRound != 0)?.windowDestroyedRound ?? 0);
        PveSettlementStatistics.Put16(common, 0x04C, PveSettlementStatistics.U16(gri, 0x4F8));
        PveSettlementStatistics.Put16(common, 0x080, PveSettlementStatistics.U16(gri, 0x508));
        PveSettlementStatistics.Put16(common, 0x082, PveSettlementStatistics.U16(gri, 0x50C));
        PveSettlementStatistics.Put16(common, 0x0B6, PveSettlementStatistics.U16(gri, 0x51C));
        PveSettlementStatistics.Put16(common, 0x0B8, PveSettlementStatistics.U16(gri, 0x520));
        PveSettlementStatistics.Put16(common, 0x0BA, PveSettlementStatistics.U16(gri, 0x524));
        Put32(common, 0x0BC, U32(gri, 0x528));
        PveSettlementStatistics.Put16(common, 0x0C0, PveSettlementStatistics.U16(gri, 0x52C));
        PveSettlementStatistics.Put16(common, 0x0C2, PveSettlementStatistics.U16(gri, 0x4F4));
        Put32(common, 0x0C4, (uint)(int)bossMaximumHealth); // Native code truncates floating-point values first, then retains four-byte integers.

        for (int i = 0; i < round; i++)
        {
            PveSettlementStatistics.Put16(common, 0x04E + i * 2, PveSettlementStatistics.U16(jerricanRounds, i * 4));
            PveSettlementStatistics.Put16(common, 0x084 + i * 2, PveSettlementStatistics.U16(autoGunRounds, i * 4));
        }

        var writer = new TdrWriter();

        PveSettlementStatistics.WriteGame(writer, common);

        return writer;
    }

    internal static void WriteEmptyRankings(TdrWriter writer, ushort mapId)
    {
        // Without authoritative historical scores, keep SVTopRecord identity slots empty rather than use current-match rankings.
        writer.U32(0x2001).U32(mapId);

        for (int level = 1; level <= 3; level++)
        {
            writer.U8(level);

            for (int rank = 0; rank < 10; rank++) writer.U64(0).String("", 32).U32(0).U32(0).U32(0);
        }
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static void Put32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
