using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Shared PVE report fields from same-version native 0x01477D00 and AF2014 TDR. Reads existing statistics
/// only; fields the function leaves unset remain zero. Each mode appends its own union after the shared
/// fields.
/// </summary>
internal static partial class PveSettlementStatistics
{
    private static readonly (int Target, int Source, int Size)[] PlayerFields =
    [
        (0x00A, 0xB98, 4), // DamageValue
        (0x018, 0x1D8, 2), // TimesBeKilled
        (0x409, 0xF4C, 2), // TimesCyborgKillerWhileHealthBelow20Percent
        (0x40B, 0xF60, 2), // CyborgKilledByJerricanCount
        (0x40D, 0xE8C, 2), // TimesAddBlood
        (0x411, 0xEBC, 4), // InGameMoney is match-local currency only.
        (0x415, 0xF50, 4), // MoneyMax
        (0x419, 0xF58, 2), // TimesUseInitiativeSkill
        (0x430, 0xDE0, 1), // Common.EnterMatchType
        (0x432, 0xC90, 4), // Common.ClientHit
        (0x436, 0xC94, 4), // Common.VerifyHit
        (0x43A, 0xC98, 4), // Common.MeleeClientHit
        (0x43E, 0xC9C, 4), // Common.MeleeVerifyHit
        (0x45C, 0xF54, 2), // SVMAYAUseSentryTimes
        (0x45E, 0xF5C, 2), // RoundCountWhileTeammateAllDead
    ];

    internal static byte[] ReadGame(uint modeId, float worldTime, byte[] game, byte[] gri,
        int playerCount, ulong bossKillerUin)
    {
        if (game.Length < 0x6A4 || gri.Length < 0x4AC || playerCount is < 0 or > 32 ||
            I32(game, 0x648) < 0 || I32(game, 0x64C) < 0)
            throw new InvalidDataException("PVE 全场统计源的结构或累计时间无效。");

        var result = new byte[0xD0];

        Put16(result, 0, Elapsed(worldTime - Single(game, 0x54C)));
        Put16(result, 4, playerCount);
        result[6] = (byte)(game[0x6A0] & 1);
        result[7] = 1;

        for (int i = 0; i < 10; i++) Put16(result, 0xA + i * 2, U16(gri, 0x474 + i * 4));

        // The native function would accumulate these counts again; the reader uses existing results only.
        Put64(result, 0x1E, (ulong)I32(game, 0x648));
        Put64(result, 0x26, (ulong)I32(game, 0x64C));
        Put64(result, 0x33, bossKillerUin);
        result[0x4A] = U32(gri, 0x32C) switch
        {
            4 or 5 or 12 or 13 => 1,
            1 => 2,
            2 => 3,
            6 or 7 or 14 => 4,
            _ => 0
        };
        Put32(result, 0xC8, U32(gri, 0x4A8));
        Put32(result, 0xCC, modeId);

        return result;
    }

    internal static byte[] ReadPlayer(uint modeId, ulong uin, float worldTime, ushort gameTime,
        byte[] pri, PveSettlementArrays arrays)
    {
        if (pri.Length < 0xF70) throw new InvalidDataException("PVE 玩家统计源尺寸不足。");

        ValidateArray(arrays.Weapons, 56, 16);
        ValidateArray(arrays.Cyborgs, 12, 16);
        ValidateArray(arrays.Pickups, 8, 10);
        ValidateArray(arrays.RolledWeapons, 16, 32);
        ValidateArray(arrays.Skills, 4, 5);
        ValidateArray(arrays.Chests, 16, 16);

        var result = new byte[0x526];
        int firstRestart = I32(pri, 0xAF8);
        ushort playedTime = firstRestart == 0 ? (ushort)0 : Elapsed(worldTime - firstRestart);
        float score = Single(pri, 0xEF4);

        if (!float.IsFinite(score) || score < 0 || score >= long.MaxValue)
            throw new InvalidDataException("PVE 原生分数无效。");

        playedTime = Math.Min(playedTime, gameTime);
        Put64(result, 0, uin);
        Put32(result, 0xE, unchecked((uint)(long)score));
        Put16(result, 0x12, playedTime);
        Put16(result, 0x24, Math.Clamp(I32(pri, 0xAFC), 0, playedTime));
        result[0x26] = (byte)((U32(pri, 0xE88) >> 5) & 1);
        Put32(result, 0x522, modeId);

        foreach (var field in PlayerFields)
            pri.AsSpan(field.Source, field.Size).CopyTo(result.AsSpan(field.Target));

        int[] proficiencyOffsets = [0xC1C, 0xC24, 0xC34, 0xC2C, 0xC3C, 0xC44, 0xC4C, 0xC54];

        Put16(result, 0x99, proficiencyOffsets.Length);

        for (int i = 0; i < proficiencyOffsets.Length; i++)
        {
            int target = 0x9B + i * 10;

            Put16(result, target, i);
            Put16(result, target + 2, U16(pri, proficiencyOffsets[i]));
            Put16(result, target + 4, U16(pri, proficiencyOffsets[i] + 4));
        }

        Put32(result, 0xD3, U32(pri, 0xEF8));
        Put32(result, 0xDD, U32(pri, 0xF00));
        Put32(result, 0xE7, U32(pri, 0xEFC));

        Put16(result, 0x13B, arrays.Weapons.Length / 56);

        for (int i = 0; i < arrays.Weapons.Length / 56; i++)
        {
            int source = i * 56, target = 0x13D + i * 29;

            Put16(result, target, U16(arrays.Weapons, source));
            Put16(result, target + 2, U16(arrays.Weapons, source + 4));
            Put16(result, target + 4, U16(arrays.Weapons, source + 8));
            result[target + 6] = (byte)(arrays.Weapons[source + 12] & 1);
            Put16(result, target + 7, U16(arrays.Weapons, source + 16));
            Put16(result, target + 9, U16(arrays.Weapons, source + 20));
            Put16(result, target + 21, U16(arrays.Weapons, source + 40));
            Put16(result, target + 23, U16(arrays.Weapons, source + 48));
            Put32(result, target + 25, U32(arrays.Weapons, source + 52));
        }

        int kills = 0, headshots = 0;

        Put16(result, 0x47, arrays.Cyborgs.Length / 12);

        for (int i = 0; i < arrays.Cyborgs.Length / 12; i++)
        {
            int source = i * 12, target = 0x49 + i * 5;

            result[target] = arrays.Cyborgs[source];
            Put16(result, target + 1, U16(arrays.Cyborgs, source + 4));
            Put16(result, target + 3, U16(arrays.Cyborgs, source + 8));
            kills = unchecked(kills + I32(arrays.Cyborgs, source + 4));
            headshots = unchecked(headshots + I32(arrays.Cyborgs, source + 8));
        }

        Put16(result, 0x16, kills);
        Put16(result, 0x1C, headshots);
        Put16(result, 0x27, arrays.Pickups.Length / 8);

        for (int i = 0; i < arrays.Pickups.Length / 8; i++)
        {
            result[0x29 + i * 3] = arrays.Pickups[i * 8];
            Put16(result, 0x2A + i * 3, U16(arrays.Pickups, i * 8 + 4));
        }

        Put16(result, 0x323, arrays.RolledWeapons.Length / 16);

        for (int i = 0; i < arrays.RolledWeapons.Length / 16; i++)
        {
            int source = i * 16, target = 0x325 + i * 7;

            Put16(result, target, arrays.RolledWeapons[source]);
            Put16(result, target + 2, U16(arrays.RolledWeapons, source + 4));
            result[target + 4] = (byte)(arrays.RolledWeapons[source + 8] & 1);
            Put16(result, target + 5, U16(arrays.RolledWeapons, source + 12));
        }

        Put16(result, 0x30D, arrays.Skills.Length / 4);
        arrays.Skills.CopyTo(result, 0x30F);
        Put16(result, 0x460, arrays.Chests.Length / 16);

        for (int i = 0; i < arrays.Chests.Length / 16; i++)
        {
            int source = i * 16, target = 0x462 + i * 12;

            Put16(result, target, U16(arrays.Chests, source));
            Put32(result, target + 2, U32(arrays.Chests, source + 4));
            Put32(result, target + 6, U32(arrays.Chests, source + 8));
            Put16(result, target + 10, U16(arrays.Chests, source + 12) & 1);
        }

        return result;
    }

    internal static ushort Elapsed(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0 || seconds >= int.MaxValue)
            throw new InvalidDataException("PVE 原生时间无效。");

        return unchecked((ushort)(int)seconds);
    }

    private static void ValidateArray(byte[] bytes, int stride, int maximum)
    {
        if (bytes.Length % stride != 0 || bytes.Length / stride > maximum)
            throw new InvalidDataException("PVE 统计数组尺寸超出客户端 TDR 范围。");
    }

    internal static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static float Single(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));
    internal static void Put16(byte[] bytes, int offset, int value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), unchecked((ushort)value));
    private static void Put32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}

/// <summary>
/// PVE dynamic statistics bounded by native/TDR limits, with array-header stability checked by the reader.
/// </summary>
internal sealed record PveSettlementArrays(byte[] Weapons, byte[] Cyborgs, byte[] Pickups,
    byte[] RolledWeapons, byte[] Skills, byte[] Chests);
