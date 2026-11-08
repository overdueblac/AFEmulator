using System.Buffers.Binary;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Supplemental Encirclement statistics from native 0x01474B10. LD whole-match/player unions are 20/3 bytes
/// respectively.
/// </summary>
internal sealed class EncirclementSettlementStatistics(byte[] common, byte terroristKills,
    byte bazookaKills, byte suicideBomberKills)
{
    internal const int MaximumRounds = 25;

    internal static EncirclementSettlementStatistics ReadPlayer(ulong uin, float worldTime, ushort gameTime,
        ushort round, byte[] pri, PveSettlementArrays arrays)
    {
        if (pri.Length < 0xF88 || round > MaximumRounds)
            throw new InvalidDataException("围剿战 PRI 统计源尺寸或关卡数超出已核验范围。");

        byte[] common = PveSettlementStatistics.ReadPlayer(0x2003, uin, worldTime, gameTime, pri, arrays);

        PveSettlementStatistics.Put16(common, 0x008, round);
        pri.AsSpan(0xF70, 4).CopyTo(common.AsSpan(0x454)); // LadenDamage → LDBossDamage。
        pri.AsSpan(0xF74, 2).CopyTo(common.AsSpan(0x45A)); // LadenTumbleCount maps to LDBossKill, preserving native definitions.

        // Three native IntProperty values report low bytes only, ordered as terrorist, rocket-launcher, and grenade-killed suicide-bomber counts.
        return new(common, pri[0xF80], pri[0xF7C], pri[0xF84]);
    }

    internal void WriteTo(TdrWriter writer)
    {
        PveSettlementStatistics.WritePlayer(writer, common);
        writer.U8(terroristKills).U8(bazookaKills).U8(suicideBomberKills);
    }

    internal static TdrWriter WriteGame(byte[] common, byte[] gri, int playerCount)
    {
        if (common.Length != 0xD0 || gri.Length < 0x4B8 || gri[0x31C] > 2 ||
            playerCount is < 0 or > AfdevEncirclementSettings.MaximumPlayers)
            throw new InvalidDataException("围剿战难度或真人统计数量超出已核验范围。");

        int round = BinaryPrimitives.ReadInt32LittleEndian(gri.AsSpan(0x4AC));
        ushort bossRemainingTime = (gri[0x4B0] & 1) == 0 ? (ushort)0 :
            PveSettlementStatistics.Elapsed(BinaryPrimitives.ReadSingleLittleEndian(gri.AsSpan(0x4B4)));

        if (round is < 0 or > MaximumRounds)
            throw new InvalidDataException("围剿战关卡数超出公共 PVE TDR 的 25 项上限。");

        common[7] = checked((byte)(gri[0x31C] + 1));
        common[0x4B] = (byte)round;
        PveSettlementStatistics.Put16(common, 0x004, playerCount);

        var writer = new TdrWriter();

        PveSettlementStatistics.WriteGame(writer, common);

        // LD leaves the first 16 bytes and shared per-level oil-barrel/machine-gun statistics unset. Keep zeros without deriving them from other modes.
        return writer.Zeros(16).U16(bossRemainingTime).U16(round);
    }

    internal static void WriteEmptyRankings(TdrWriter writer, ushort mapId)
    {
        // Without authoritative history, write empty LDTopRecord identity slots. Current-match scores cannot become historical records.
        writer.U32(0x2003).U32(mapId);

        for (int level = 1; level <= 3; level++)
        {
            writer.U8(level);

            for (int rank = 0; rank < 10; rank++) writer.U64(0).String("", 32).U32(0).U32(0).U32(0);
        }
    }
}
