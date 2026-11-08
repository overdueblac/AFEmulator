using System.Buffers.Binary;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Bounded encoding for this build's ZN2CPVPStatis, sending only the client's 114-byte personal statistics
/// with TDR-counted variable arrays.
/// </summary>
internal sealed class PvpStatistics(ulong uin, byte camp, ushort gameTime, ushort kills,
    ushort deaths, uint score, byte[] special)
{
    // The first 74 PVPPlayerSpecStatis bytes map from 0x01490300–0x01490800 and team-specific 0x01465B90.
    private static readonly (int Target, int Source)[] SpecialWords =
    [
        (0, 0xEA0),
        (4, 0xBC0),
        (6, 0xBC4),
        (8, 0xC4C),
        (10, 0xC50),
        (12, 0xBA0),
        (14, 0xBA4),
        (16, 0xBA8),
        (18, 0xBAC),
        (20, 0xBB0),
        (22, 0xBB4),
        (24, 0xF34),
        (28, 0xE98),
        (30, 0xE9C),
        (32, 0xE90),
        (34, 0xE94),
        (36, 0xBC8),
        (38, 0xBCC),
        (40, 0xBFC),
        (42, 0xC00),
        (44, 0xC04),
        (46, 0xC08),
        (48, 0xC0C),
        (50, 0xC10),
        (52, 0xC14),
        (54, 0xF3C),
        (56, 0xF44),
        (70, 0xBDC),
        (72, 0xBE0),
    ];

    internal byte Camp => camp;

    internal static PvpStatistics ReadPlayer(ulong uin, byte camp, float worldTime, byte[] pri, bool teamDeathmatch = true, IReadOnlyDictionary<int, ushort>? modeWords = null)
    {
        if (pri.Length < 0xF60 || camp > 1) throw new InvalidDataException("团队个人统计源无效。");

        int firstRestart = BinaryPrimitives.ReadInt32LittleEndian(pri.AsSpan(0xAF8));
        float elapsed = firstRestart == 0 ? 0 : worldTime - firstRestart;
        float score = BinaryPrimitives.ReadSingleLittleEndian(pri.AsSpan(0x1D4));
        var special = new byte[93];

        if (!float.IsFinite(elapsed) || elapsed < 0 || elapsed >= int.MaxValue ||
            !float.IsFinite(score) || score < int.MinValue || score >= int.MaxValue)
            throw new InvalidDataException("团队个人时间或原生得分无效。");

        foreach (var (target, source) in SpecialWords)
            if (teamDeathmatch || source < 0xF30) pri.AsSpan(source, 2).CopyTo(special.AsSpan(target));
        if (modeWords is not null)
            foreach (var (offset, value) in modeWords)
                BinaryPrimitives.WriteUInt16LittleEndian(special.AsSpan(offset), value);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(pri.AsSpan(0xEC0));

        for (int index = 0; index < 9; index++)
            special[74 + index] = (flags & (2u << index)) != 0 ? (byte)1 : (byte)0;
        special[88] = 0; // Native reporting depends on weapon-specialization aggregates. Without sampling that array, do not invent all-weapon achievements.

        return new(uin, camp, unchecked((ushort)(int)elapsed), U16(pri, 0xBE4), U16(pri, 0x1D8),
            unchecked((uint)(int)score), special);
    }

    internal static TdrWriter WriteGame(byte[] game, byte[] gri, int playerCount, byte winningCamp,
        ushort[] teamScores, ulong ace, byte[]? rounds = null)
    {
        int totalTime = BinaryPrimitives.ReadInt32LittleEndian(game.AsSpan(0x648));
        int playerTimes = BinaryPrimitives.ReadInt32LittleEndian(game.AsSpan(0x64C));
        ushort seconds = (gri[0x248] & 1) != 0 ? (ushort)0 : unchecked((ushort)(U16(gri, 0x1F0) - U16(gri, 0x1E0)));

        if (playerCount is < 0 or > 32 || winningCamp > 2 || teamScores.Length != 2 || totalTime < 0 || playerTimes < 0)
            throw new InvalidDataException("团队全场统计无效。");

        rounds ??= [];

        if (rounds.Length % 22 != 0 || rounds.Length / 22 > 32)
            throw new InvalidDataException("爆破小局统计超出 TDR 边界。");

        // Unpersisted titles remain zero. ACE comes from native LeadPRI; per-round Bomb records come from this match's GRI.
        return new TdrWriter().U16(seconds).U16(playerCount).U16(teamScores[0]).U16(teamScores[1])
            .U8(winningCamp).U64(0).U64(ace).U64(0).U64(0).U64(0).U64(0)
            .U16(rounds.Length / 22).U8(0).U64((ulong)totalTime).U64((ulong)playerTimes).U8(0).U16(0).U64(0).Bytes(rounds);
    }

    internal void WriteTo(TdrWriter writer)
    {
        writer.U64(uin).U8(camp).U16(0).U16(gameTime).U16(kills).U16(deaths).U32(score);
        for (int offset = 0; offset < 74; offset += 2) writer.U16(U16(special, offset));
        writer.Bytes(special.AsSpan(74));
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
}
