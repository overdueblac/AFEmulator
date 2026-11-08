using System.Buffers.Binary;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Bounded BIOPlayerStatis host layout and wire encoding. Client TDR defines field widths/array limits;
/// 0x0146AB46 defines PRI mapping. Fields absent from native reports keep initialized values; never
/// fabricate statistics.
/// </summary>
internal sealed class BioSettlementStatistics(byte[] report)
{
    private static readonly (int Target, int Source, int Size)[] PlayerFields =
    [
        (0x008, 0x0FD0, 4), // Score
        (0x011, 0x0B98, 4), // DamageValue
        (0x015, 0x10C0, 2), // RoundCount
        (0x017, 0x0FD8, 2), // TimesKill
        (0x019, 0x0FEC, 2), // TimesBeKilled
        (0x01B, 0x0FC4, 2), // SurviveCount
        (0x01D, 0x0FDC, 2), // GameCoin
        (0x035, 0x10AC, 2), // TimesHeadShot
        (0x037, 0x10A8, 2), // TimesKillZombieNoRebirth
        (0x039, 0x1024, 2), // TimesHuman
        (0x03B, 0x0FA8, 2), // TimesHumanKillZombie
        (0x03D, 0x10A4, 2), // TimesHumanDamageZombie
        (0x03F, 0x1028, 2), // TimesHero
        (0x041, 0x102C, 2), // TimesHeroWin
        (0x043, 0x1030, 2), // TimesHeroLose
        (0x045, 0x1034, 2), // TimesHeroKillZombie
        (0x047, 0x1038, 2), // TimesZombie
        (0x049, 0x103C, 2), // TimesZombieInfeHuman
        (0x04B, 0x1040, 2), // TimesZombieBeKilled
        (0x0F9, 0x0FC0, 2), // TimesTerminatorKillHero
        (0x0FB, 0x1054, 2), // TimesTerminator
        (0x0FD, 0x1058, 2), // TimesTerminatorInfeHuman
        (0x0FF, 0x105C, 2), // TimesBeZombie30
        (0x101, 0x1060, 2), // TimesBeZombie30To45
        (0x103, 0x1064, 2), // TimesBeZombie45To60
        (0x105, 0x1068, 2), // TimesBeZombie60To75
        (0x107, 0x106C, 2), // TimesBeZombie75To90
        (0x109, 0x1070, 2), // TimesBeZombie90To105
        (0x10B, 0x1074, 2), // TimesBeZombie105To120
        (0x10D, 0x1078, 2), // TimesBeZombie120To135
        (0x10F, 0x107C, 2), // TimesBeZombie135To150
        (0x111, 0x1080, 2), // TimesBeHero30
        (0x113, 0x1084, 2), // TimesBeHero30To45
        (0x115, 0x1088, 2), // TimesBeHero45To60
        (0x117, 0x108C, 2), // TimesBeHero60To75
        (0x119, 0x1090, 2), // TimesBeHero75To90
        (0x11B, 0x1094, 2), // TimesBeHero90To105
        (0x11D, 0x1098, 2), // TimesBeHero105To120
        (0x11F, 0x109C, 2), // TimesBeHero120To135
        (0x121, 0x10A0, 2), // TimesBeHero135To150
        (0x2E5, 0x0DE0, 1), // Common.EnterMatchType
        (0x2E7, 0x0C90, 4), // Common.ClientHit
        (0x2EB, 0x0C94, 4), // Common.VerifyHit
        (0x2EF, 0x0C98, 4), // Common.MeleeClientHit
        (0x2F3, 0x0C9C, 4), // Common.MeleeVerifyHit
        (0x2F7, 0x0F5C, 2), // TimesZombieWin
        (0x2F9, 0x0F60, 2), // TimesHumanWin
        (0x2FB, 0x0FC4, 2), // TimesHumanAlive
        (0x2FD, 0x0FCC, 4), // HumanDamageValue
        (0x303, 0x1034, 2), // TimesHeroKill
        (0x305, 0x10B0, 2), // TimesHeroUseGrenadier
        (0x307, 0x10B4, 2), // TimesHeroUseKnife
        (0x309, 0x10B8, 2), // HeroGrenadierKill
        (0x30B, 0x10BC, 2), // HeroKnifeKill
        (0x30D, 0x10D4, 4), // HeroGrenadierDamage
        (0x311, 0x10D0, 4), // HeroKnifeDamage
        (0x315, 0x0FF4, 2), // AchStatis.TimesZombieInfeHumanLowhealth
        (0x318, 0x1004, 2), // AchStatis.MaxTimesZombieInfeHumanOneRound
        (0x31A, 0x1008, 2), // AchStatis.TimesCollectSupplybox
        (0x31C, 0x0FFC, 2), // AchStatis.TimesHumanKillMeleeweapon
    ];

    internal byte Camp => Read8(12);
    internal ushort GameCoin => Read16(29);

    internal static BioSettlementStatistics ReadPlayer(ulong uin, byte camp, float worldTime,
        byte[] pri, byte[] zombies, byte[] weapons)
    {
        if (pri.Length < 0x10D8 || zombies.Length % 28 != 0 || zombies.Length > 12 * 28 ||
            weapons.Length % 144 != 0 || weapons.Length > 16 * 144)
            throw new InvalidDataException("BIO 结算统计源的结构尺寸无效。");

        var result = new byte[798];
        int firstRestart = BinaryPrimitives.ReadInt32LittleEndian(pri.AsSpan(0xAF8));
        float elapsed = firstRestart == 0 ? 0 : worldTime - firstRestart;

        if (!float.IsFinite(elapsed) || elapsed < 0 || elapsed >= int.MaxValue)
            throw new InvalidDataException("BIO 个人参战时间无效。");

        ushort gameTime = unchecked((ushort)(int)elapsed);

        BinaryPrimitives.WriteUInt64LittleEndian(result, uin);
        result[12] = camp;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(13), gameTime);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(15), Math.Min(U16(pri, 0xAFC), gameTime));

        foreach (var field in PlayerFields)
            pri.AsSpan(field.Source, field.Size).CopyTo(result.AsSpan(field.Target));

        // Preserve native-report semantics for both low-16-bit sums and the final-shot kill flag.
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0xF7), unchecked((ushort)(U16(pri, 0xFBC) + U16(pri, 0xFC0))));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x301), unchecked((ushort)(U16(pri, 0xFB0) + U16(pri, 0xFA8))));
        result[0x317] = BinaryPrimitives.ReadInt32LittleEndian(pri.AsSpan(0xFF8)) > 0 ? (byte)1 : (byte)0;

        int zombieCount = zombies.Length / 28;
        int weaponCount = weapons.Length / 144;
        int[] weaponWords = [0, 4, 8, 12, 16, 108, 96, 112];

        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(77), checked((ushort)zombieCount));
        for (int i = 0; i < zombieCount; i++)
            for (int field = 0; field < 7; field++)
                zombies.AsSpan(i * 28 + field * 4, 2).CopyTo(result.AsSpan(79 + i * 14 + field * 2));

        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(291), checked((ushort)weaponCount));
        for (int i = 0; i < weaponCount; i++)
        {
            for (int field = 0; field < weaponWords.Length; field++)
                weapons.AsSpan(i * 144 + weaponWords[field], 2).CopyTo(result.AsSpan(293 + i * 28 + field * 2));

            weapons.AsSpan(i * 144 + 24, 12).CopyTo(result.AsSpan(293 + i * 28 + 16));
        }

        return new BioSettlementStatistics(result);
    }

    internal static TdrWriter WriteGame(byte[] game, byte[] gri, int playerCount, byte winningCamp)
    {
        float postHeroTime = BinaryPrimitives.ReadSingleLittleEndian(gri.AsSpan(0x3CC));
        int totalTime = BinaryPrimitives.ReadInt32LittleEndian(game.AsSpan(0x648));
        int playerTimes = BinaryPrimitives.ReadInt32LittleEndian(game.AsSpan(0x64C));

        if (!float.IsFinite(postHeroTime) || postHeroTime < 0 || postHeroTime >= ulong.MaxValue ||
            totalTime < 0 || playerTimes < 0 || playerCount is < 0 or > 32)
            throw new InvalidDataException("BIO 全场统计超出原生有效范围。");

        // This native report never assigns ACEUin. Read total time/player counts without repeating native accumulation.
        return new TdrWriter().U16(U16(gri, 0x3B0)).U16(playerCount).U16(U16(gri, 0x3A8))
            .U16(U16(gri, 0x3AC)).U8(winningCamp).U64(0).U16(U16(gri, 0x388))
            .U64((ulong)totalTime).U64((ulong)playerTimes).U64((ulong)postHeroTime);
    }

    internal void WriteTo(TdrWriter writer)
    {
        // Wire arrays vary by count; the fixed 798-byte host structure cannot be written directly to A11B.
        writer.U64(Read64(0x000)); // Uin
        writer.U32(Read32(0x008)); // Score
        writer.U8(Read8(0x00C)); // PlayerCamp
        writer.U16(Read16(0x00D)); // GameTime
        writer.U16(Read16(0x00F)); // SurvivalTime
        writer.U32(Read32(0x011)); // DamageValue
        writer.U16(Read16(0x015)); // RoundCount
        writer.U16(Read16(0x017)); // TimesKill
        writer.U16(Read16(0x019)); // TimesBeKilled
        writer.U16(Read16(0x01B)); // SurviveCount
        writer.U16(Read16(0x01D)); // GameCoin
        writer.U16(Read16(0x01F)); // SkillCount
        for (int i = 0; i < Read16(0x01F); i++)
        {
            writer.U32(Read32(0x021 + i * 4)); // SkillItemIds
        }
        writer.U16(Read16(0x035)); // TimesHeadShot
        writer.U16(Read16(0x037)); // TimesKillZombieNoRebirth
        writer.U16(Read16(0x039)); // TimesHuman
        writer.U16(Read16(0x03B)); // TimesHumanKillZombie
        writer.U16(Read16(0x03D)); // TimesHumanDamageZombie
        writer.U16(Read16(0x03F)); // TimesHero
        writer.U16(Read16(0x041)); // TimesHeroWin
        writer.U16(Read16(0x043)); // TimesHeroLose
        writer.U16(Read16(0x045)); // TimesHeroKillZombie
        writer.U16(Read16(0x047)); // TimesZombie
        writer.U16(Read16(0x049)); // TimesZombieInfeHuman
        writer.U16(Read16(0x04B)); // TimesZombieBeKilled
        writer.U16(Read16(0x04D)); // ZombieInfoCount
        for (int i = 0; i < Read16(0x04D); i++)
        {
            writer.U16(Read16(0x04F + i * 14)); // ZombieInfo.Type
            writer.U16(Read16(0x051 + i * 14)); // ZombieInfo.Level
            writer.U16(Read16(0x053 + i * 14)); // ZombieInfo.TimesUse
            writer.U16(Read16(0x055 + i * 14)); // ZombieInfo.TimesInfection
            writer.U16(Read16(0x057 + i * 14)); // ZombieInfo.TimesBeKilled
            writer.U16(Read16(0x059 + i * 14)); // ZombieInfo.TimesRebirthKilled
            writer.U16(Read16(0x05B + i * 14)); // ZombieInfo.PlayTime
        }
        writer.U16(Read16(0x0F7)); // TimesZombieKillHero
        writer.U16(Read16(0x0F9)); // TimesTerminatorKillHero
        writer.U16(Read16(0x0FB)); // TimesTerminator
        writer.U16(Read16(0x0FD)); // TimesTerminatorInfeHuman
        writer.U16(Read16(0x0FF)); // TimesBeZombie30
        writer.U16(Read16(0x101)); // TimesBeZombie30To45
        writer.U16(Read16(0x103)); // TimesBeZombie45To60
        writer.U16(Read16(0x105)); // TimesBeZombie60To75
        writer.U16(Read16(0x107)); // TimesBeZombie75To90
        writer.U16(Read16(0x109)); // TimesBeZombie90To105
        writer.U16(Read16(0x10B)); // TimesBeZombie105To120
        writer.U16(Read16(0x10D)); // TimesBeZombie120To135
        writer.U16(Read16(0x10F)); // TimesBeZombie135To150
        writer.U16(Read16(0x111)); // TimesBeHero30
        writer.U16(Read16(0x113)); // TimesBeHero30To45
        writer.U16(Read16(0x115)); // TimesBeHero45To60
        writer.U16(Read16(0x117)); // TimesBeHero60To75
        writer.U16(Read16(0x119)); // TimesBeHero75To90
        writer.U16(Read16(0x11B)); // TimesBeHero90To105
        writer.U16(Read16(0x11D)); // TimesBeHero105To120
        writer.U16(Read16(0x11F)); // TimesBeHero120To135
        writer.U16(Read16(0x121)); // TimesBeHero135To150
        writer.U16(Read16(0x123)); // WeaponCount
        for (int i = 0; i < Read16(0x123); i++)
        {
            writer.U16(Read16(0x125 + i * 28)); // WeaponStat.ProficientType
            writer.U16(Read16(0x127 + i * 28)); // WeaponStat.WeaponType
            writer.U16(Read16(0x129 + i * 28)); // WeaponStat.WeaponId
            writer.U16(Read16(0x12B + i * 28)); // WeaponStat.TimesKill
            writer.U16(Read16(0x12D + i * 28)); // WeaponStat.TimesBeKill
            writer.U16(Read16(0x12F + i * 28)); // WeaponStat.UseCount
            writer.U16(Read16(0x131 + i * 28)); // WeaponStat.TimesUseUp
            writer.U16(Read16(0x133 + i * 28)); // WeaponStat.UseTime
            writer.U32(Read32(0x135 + i * 28)); // WeaponStat.DamageValue
            writer.U32(Read32(0x139 + i * 28)); // WeaponStat.NumberShotBullet
            writer.U32(Read32(0x13D + i * 28)); // WeaponStat.NumberShotBulletHit
        }
        writer.U8(Read8(0x2E5)); // Common.EnterMatchType
        writer.U8(Read8(0x2E6)); // Common.QuitMatchType
        writer.U32(Read32(0x2E7)); // Common.ClientHit
        writer.U32(Read32(0x2EB)); // Common.VerifyHit
        writer.U32(Read32(0x2EF)); // Common.MeleeClientHit
        writer.U32(Read32(0x2F3)); // Common.MeleeVerifyHit
        writer.U16(Read16(0x2F7)); // TimesZombieWin
        writer.U16(Read16(0x2F9)); // TimesHumanWin
        writer.U16(Read16(0x2FB)); // TimesHumanAlive
        writer.U32(Read32(0x2FD)); // HumanDamageValue
        writer.U16(Read16(0x301)); // TimesHumanKill
        writer.U16(Read16(0x303)); // TimesHeroKill
        writer.U16(Read16(0x305)); // TimesHeroUseGrenadier
        writer.U16(Read16(0x307)); // TimesHeroUseKnife
        writer.U16(Read16(0x309)); // HeroGrenadierKill
        writer.U16(Read16(0x30B)); // HeroKnifeKill
        writer.U32(Read32(0x30D)); // HeroGrenadierDamage
        writer.U32(Read32(0x311)); // HeroKnifeDamage
        writer.U16(Read16(0x315)); // AchStatis.TimesZombieInfeHumanLowhealth
        writer.U8(Read8(0x317)); // AchStatis.KillZombieUseLastBullet
        writer.U16(Read16(0x318)); // AchStatis.MaxTimesZombieInfeHumanOneRound
        writer.U16(Read16(0x31A)); // AchStatis.TimesCollectSupplybox
        writer.U16(Read16(0x31C)); // AchStatis.TimesHumanKillMeleeweapon
    }

    private byte Read8(int offset) => report[offset];
    private ushort Read16(int offset) => U16(report, offset);
    private uint Read32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(offset));
    private ulong Read64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(report.AsSpan(offset));
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
}
