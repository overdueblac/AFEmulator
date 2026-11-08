using System.Buffers.Binary;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Supplemental Tower Defense statistics from native 0x01474D80, reporting trap-array entries 1–16 with
/// this TDR's widths/overflow semantics.
/// </summary>
internal sealed class TowerDefenseSettlementStatistics(byte[] common, ushort teleports,
    uint killedOrcs, uint killedFlyOrcs, uint killedPriests, TowerDefenseSettlementStatistics.Trap[] traps)
{
    private readonly Trap[] playerTraps = traps;

    internal static TowerDefenseSettlementStatistics ReadPlayer(ulong uin, float worldTime, ushort gameTime,
        ushort round, byte[] pri, PveSettlementArrays arrays)
    {
        if (pri.Length < 0x1118) throw new InvalidDataException("塔防 PRI 陷阱统计源尺寸不足。");

        byte[] common = PveSettlementStatistics.ReadPlayer(0x2005, uin, worldTime, gameTime, pri, arrays);
        var traps = new Trap[16];

        PveSettlementStatistics.Put16(common, 8, round);

        for (int i = 0; i < traps.Length; i++)
        {
            // Reflected array base is 0xF80. Native reporting skips entry 0 and reads from 0xF98.
            int offset = 0xF98 + i * 24;
            float time = BinaryPrimitives.ReadSingleLittleEndian(pri.AsSpan(offset + 16));

            _ = PveSettlementStatistics.Elapsed(time);
            traps[i] = new Trap(I32(pri, offset), U32(pri, offset + 4), U16(pri, offset + 8),
                U32(pri, offset + 12), time, U32(pri, offset + 20));
        }

        return new(common, U16(pri, 0xF7C), U32(pri, 0xE80), U32(pri, 0xF70), U32(pri, 0xF74), traps);
    }

    internal void WriteTo(TdrWriter writer)
    {
        PveSettlementStatistics.WritePlayer(writer, common);
        writer.U16(teleports);

        foreach (var trap in playerTraps) writer.U8(trap.Used > 0 ? 1 : 0);

        writer.U32(killedOrcs).U32(killedFlyOrcs).U32(killedPriests);

        foreach (var trap in playerTraps)
            WriteTrap(writer, trap.Used, (int)trap.Sold, trap.Killed,
                PveSettlementStatistics.Elapsed(trap.Time), trap.Damage);
    }

    internal static TdrWriter WriteGame(byte[] common, byte[] gri,
        IReadOnlyList<TowerDefenseSettlementStatistics> players)
    {
        if (gri.Length < 0x4B4 || gri[0x31C] > 2 || I32(gri, 0x4AC) is < 0 or > 25 || players.Count > 4)
            throw new InvalidDataException("塔防难度、波次或真人统计数量超出已核验范围。");

        var writer = new TdrWriter();
        uint destroyed = 0, recycled = 0;

        common[7] = checked((byte)(gri[0x31C] + 1));
        common[0x4B] = (byte)I32(gri, 0x4AC);
        PveSettlementStatistics.WriteGame(writer, common);

        for (int i = 0; i < 16; i++)
        {
            int used = 0, sold = 0, killed = 0;
            ushort time = 0;
            uint damage = 0;

            foreach (var player in players)
            {
                var trap = player.playerTraps[i];

                used = unchecked(used + trap.Used);
                sold = unchecked(sold + (int)trap.Sold);
                killed += trap.Killed;
                time = PveSettlementStatistics.Elapsed(time + trap.Time);
                damage = unchecked(damage + trap.Damage);
                destroyed = unchecked(destroyed + trap.Destroyed);
                recycled = unchecked(recycled + trap.Sold);
            }

            WriteTrap(writer, used, sold, killed, time, damage);
        }

        writer.U32(destroyed).U32(recycled).U32(U32(gri, 0x4B0));

        return writer;
    }

    internal static void WriteEmptyRankings(TdrWriter writer, ushort mapId)
    {
        // No historical Tower Defense leaderboard store exists. Fixed slots use empty UIN/nickname, never current scores as historical rankings.
        writer.U32(0x2005).U32(mapId);

        for (int level = 1; level <= 3; level++)
        {
            writer.U8(level);

            for (int rank = 0; rank < 10; rank++) writer.U64(0).String("", 32).U32(0).U32(0).U32(0);
        }
    }

    private static void WriteTrap(TdrWriter writer, int used, int sold, int killed, ushort time, uint damage)
        => writer.U16(used).U16(sold).U16(killed).U16(time).U32(damage);
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    internal sealed record Trap(int Used, uint Sold, ushort Killed, uint Destroyed, float Time, uint Damage);
}
