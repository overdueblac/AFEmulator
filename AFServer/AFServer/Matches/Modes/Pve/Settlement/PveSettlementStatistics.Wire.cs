using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

internal static partial class PveSettlementStatistics
{
    // AF2014 shared PVE TDR fields use network byte order and refer counts; the concrete mode appends its union.
    internal static void WriteGame(TdrWriter writer, byte[] report)
    {
        if (report.Length != 0xD0) throw new InvalidDataException("PVE 公共统计结构尺寸无效。");

        if (report[0x04B] > 25)
            throw new InvalidDataException("PVE 统计数量超出客户端 TDR 上限。");

        writer.U16(U16(report, 0x000)); // GameTime
        writer.U16(U16(report, 0x002)); // MaxRunawayGameTime
        writer.U16(U16(report, 0x004)); // PlayerCount
        writer.U8(report[0x006]); // IsWin
        writer.U8(report[0x007]); // Level
        writer.U16(U16(report, 0x008)); // WindowDestoryRound
        writer.U16(U16(report, 0x00A)); // TimesRebirthCoin
        writer.U16(U16(report, 0x00C)); // TimesMechLicense
        writer.U16(U16(report, 0x00E)); // TurretMK38Sec
        writer.U16(U16(report, 0x010)); // TurretMK38Times
        writer.U16(U16(report, 0x012)); // TurretArtillerySec
        writer.U16(U16(report, 0x014)); // TurretArtilleryTimes
        writer.U16(U16(report, 0x016)); // BossTurretMK38Sec
        writer.U16(U16(report, 0x018)); // BossTurretMK38Times
        writer.U16(U16(report, 0x01A)); // BossTurretArtillerySec
        writer.U16(U16(report, 0x01C)); // BossTurretArtilleryTimes
        writer.U64(U64(report, 0x01E)); // TotalTime
        writer.U64(U64(report, 0x026)); // PlayerTimes
        writer.U16(U16(report, 0x02E)); // IFGameTime
        writer.U8(report[0x030]); // FightBoss
        writer.U8(report[0x031]); // IsRedBlood
        writer.U8(report[0x032]); // IsGateDamage
        writer.U64(U64(report, 0x033)); // UinWhoKillBoss
        writer.U8(report[0x03B]); // IFStepBossPlayerCount
        writer.U8(report[0x03C]); // IFStep1PlayerCount
        writer.U8(report[0x03D]); // IFStep2PlayerCount
        writer.U8(report[0x03E]); // IFStep1BossBloodPerc
        writer.U8(report[0x03F]); // IFStep2BossBloodPerc
        writer.U16(U16(report, 0x040)); // IFStep1RemainingTime
        writer.U16(U16(report, 0x042)); // IFStep2RemainingTime
        writer.U8(report[0x044]); // IFPlayerCountBegin
        writer.U8(report[0x045]); // IFPlayerCount5Min
        writer.U8(report[0x046]); // IFPlayerCount10Min
        writer.U8(report[0x047]); // IFStep1And10MinGateBloodPerc
        writer.U8(report[0x048]); // IFStep1WinBaseBloodPerc
        writer.U8(report[0x049]); // IFStep2WinBaseBloodPerc
        writer.U8(report[0x04A]); // Reason
        writer.U8(report[0x04B]); // RoundCount
        writer.U16(U16(report, 0x04C)); // JerricanBombCount
        for (int i = 0; i < report[0x04B]; i++)
        {
            writer.U16(U16(report, 0x04E + i * 2)); // JerricanBombCountArray
        }
        writer.U16(U16(report, 0x080)); // MonsterKilledByJerricanCount
        writer.U16(U16(report, 0x082)); // AutoGunUsedCount
        for (int i = 0; i < report[0x04B]; i++)
        {
            writer.U16(U16(report, 0x084 + i * 2)); // AutoGunUsedCountArray
        }
        writer.U16(U16(report, 0x0B6)); // MonsterKilledByAutoGunCount
        writer.U16(U16(report, 0x0B8)); // BMBeginPeopleCount
        writer.U16(U16(report, 0x0BA)); // BMOverPeopleCount
        writer.U32(U32(report, 0x0BC)); // BMOverHealth
        writer.U16(U16(report, 0x0C0)); // BMOverPeriod
        writer.U16(U16(report, 0x0C2)); // KillBigMonsterUpTime
        writer.U32(U32(report, 0x0C4)); // BigMonsterHealthMax
        writer.U32(U32(report, 0x0C8)); // BossLowestHealthPercent
        writer.U32(U32(report, 0x0CC)); // ModID
    }

    internal static void WritePlayer(TdrWriter writer, byte[] report)
    {
        if (report.Length != 0x526) throw new InvalidDataException("PVE 公共统计结构尺寸无效。");

        if (U16(report, 0x027) > 10 ||
            U16(report, 0x047) > 16 ||
            U16(report, 0x099) > 16 ||
            U16(report, 0x13B) > 16 ||
            U16(report, 0x30D) > 5 ||
            U16(report, 0x323) > 32 ||
            U16(report, 0x460) > 16)
            throw new InvalidDataException("PVE 统计数量超出客户端 TDR 上限。");

        writer.U64(U64(report, 0x000)); // Uin
        writer.U16(U16(report, 0x008)); // RoundCount
        writer.U32(U32(report, 0x00A)); // DamageValue
        writer.U32(U32(report, 0x00E)); // Score
        writer.U16(U16(report, 0x012)); // GameTime
        writer.U16(U16(report, 0x014)); // GameTimeScore
        writer.U16(U16(report, 0x016)); // TimesKill
        writer.U16(U16(report, 0x018)); // TimesBeKilled
        writer.U16(U16(report, 0x01A)); // TimesBeKilledScore
        writer.U16(U16(report, 0x01C)); // TimesHeadshot
        writer.U16(U16(report, 0x01E)); // TimesHeadshotScore
        writer.U16(U16(report, 0x020)); // TimesSpecKill
        writer.U16(U16(report, 0x022)); // TimesSpecKillScore
        writer.U16(U16(report, 0x024)); // SurvivalTime
        writer.U8(report[0x026]); // RollRandomWeapon
        writer.U16(U16(report, 0x027)); // PickupPropCount
        for (int i = 0; i < U16(report, 0x027); i++)
        {
            writer.U8(report[0x029 + i * 3]); // PickupPropList.PickupPropType
            writer.U16(U16(report, 0x02A + i * 3)); // PickupPropList.PickupPropCount
        }
        writer.U16(U16(report, 0x047)); // CyborgTypeCount
        for (int i = 0; i < U16(report, 0x047); i++)
        {
            writer.U8(report[0x049 + i * 5]); // CyborgList.CyborgType
            writer.U16(U16(report, 0x04A + i * 5)); // CyborgList.KillCount
            writer.U16(U16(report, 0x04C + i * 5)); // CyborgList.HeadShotCount
        }
        writer.U16(U16(report, 0x099)); // WeaponProficientCount
        for (int i = 0; i < U16(report, 0x099); i++)
        {
            writer.U16(U16(report, 0x09B + i * 10)); // WeaponProficientStatis.ProficientType
            writer.U16(U16(report, 0x09D + i * 10)); // WeaponProficientStatis.TimesKill
            writer.U16(U16(report, 0x09F + i * 10)); // WeaponProficientStatis.TimesHeadShot
            writer.U32(U32(report, 0x0A1 + i * 10)); // WeaponProficientStatis.Record
        }
        writer.U16(U16(report, 0x13B)); // WeaponCount
        for (int i = 0; i < U16(report, 0x13B); i++)
        {
            writer.U16(U16(report, 0x13D + i * 29)); // WeaponStatis.ProficientType
            writer.U16(U16(report, 0x13F + i * 29)); // WeaponStatis.WeaponType
            writer.U16(U16(report, 0x141 + i * 29)); // WeaponStatis.WeaponId
            writer.U8(report[0x143 + i * 29]); // WeaponStatis.IsSuperWeapon
            writer.U16(U16(report, 0x144 + i * 29)); // WeaponStatis.TimesKill
            writer.U16(U16(report, 0x146 + i * 29)); // WeaponStatis.TimesBeKill
            writer.U16(U16(report, 0x148 + i * 29)); // WeaponStatis.TimesHeadShot
            writer.U32(U32(report, 0x14A + i * 29)); // WeaponStatis.NumberShotBullet
            writer.U32(U32(report, 0x14E + i * 29)); // WeaponStatis.NumberShotBulletHit
            writer.U16(U16(report, 0x152 + i * 29)); // WeaponStatis.UseTime
            writer.U16(U16(report, 0x154 + i * 29)); // WeaponStatis.UseCount
            writer.U32(U32(report, 0x156 + i * 29)); // WeaponStatis.DamageValue
        }
        writer.U16(U16(report, 0x30D)); // SkillCount
        for (int i = 0; i < U16(report, 0x30D); i++)
        {
            writer.U32(U32(report, 0x30F + i * 4)); // SkillItemIds
        }
        writer.U16(U16(report, 0x323)); // RollWeaponCount
        for (int i = 0; i < U16(report, 0x323); i++)
        {
            writer.U16(U16(report, 0x325 + i * 7)); // RollWeaponStatis.WeaponType
            writer.U16(U16(report, 0x327 + i * 7)); // RollWeaponStatis.WeaponId
            writer.U8(report[0x329 + i * 7]); // RollWeaponStatis.IsSuperWeapon
            writer.U16(U16(report, 0x32A + i * 7)); // RollWeaponStatis.Round
        }
        writer.U16(U16(report, 0x405)); // MendWindowTimes
        writer.U16(U16(report, 0x407)); // TimesCyborgKiller
        writer.U16(U16(report, 0x409)); // TimesCyborgKillerWhileHealthBelow20Percent
        writer.U16(U16(report, 0x40B)); // CyborgKilledByJerricanCount
        writer.U16(U16(report, 0x40D)); // TimesAddBlood
        writer.U16(U16(report, 0x40F)); // TimesAddBloodScore
        writer.U32(U32(report, 0x411)); // InGameMoney
        writer.U32(U32(report, 0x415)); // MoneyMax
        writer.U16(U16(report, 0x419)); // TimesUseInitiativeSkill
        writer.U16(U16(report, 0x41B)); // IFStatis.TimesKillFly
        writer.U16(U16(report, 0x41D)); // IFStatis.TimesKillZA5
        writer.U16(U16(report, 0x41F)); // IFStatis.TimesKillMainArtillery
        writer.U16(U16(report, 0x421)); // IFStatis.TimesKillMachinegun
        writer.U16(U16(report, 0x423)); // IFStatis.TimesCallClusterMortar
        writer.U16(U16(report, 0x425)); // IFStatis.TimesKillThrownWeapon
        writer.U16(U16(report, 0x427)); // IFStatis.TimesKillHeadShot
        writer.U8(report[0x429]); // IFStatis.IsUseMecha
        writer.U16(U16(report, 0x42A)); // IFStatis.TimesKillMecha
        writer.U16(U16(report, 0x42C)); // IFStatis.MechaKillScore
        writer.U8(report[0x42E]); // IFStatis.IsMechaDamaged
        writer.U8(report[0x42F]); // IFStatis.UsedMachine
        writer.U8(report[0x430]); // Common.EnterMatchType
        writer.U8(report[0x431]); // Common.QuitMatchType
        writer.U32(U32(report, 0x432)); // Common.ClientHit
        writer.U32(U32(report, 0x436)); // Common.VerifyHit
        writer.U32(U32(report, 0x43A)); // Common.MeleeClientHit
        writer.U32(U32(report, 0x43E)); // Common.MeleeVerifyHit
        writer.U32(U32(report, 0x442)); // IFBossDamage
        writer.U16(U16(report, 0x446)); // IFBossBeKilled
        writer.U16(U16(report, 0x448)); // IFBossTurret
        writer.U32(U32(report, 0x44A)); // SVBossDamage
        writer.U16(U16(report, 0x44E)); // SVBossBeKilled
        writer.U16(U16(report, 0x450)); // SVBossKill
        writer.U16(U16(report, 0x452)); // TimesRebirthCoin
        writer.U32(U32(report, 0x454)); // LDBossDamage
        writer.U16(U16(report, 0x458)); // LDBossBeKilled
        writer.U16(U16(report, 0x45A)); // LDBossKill
        writer.U16(U16(report, 0x45C)); // SVMAYAUseSentryTimes
        writer.U16(U16(report, 0x45E)); // RoundCountWhileTeammateAllDead
        writer.U16(U16(report, 0x460)); // ChestPropCount
        for (int i = 0; i < U16(report, 0x460); i++)
        {
            writer.U16(U16(report, 0x462 + i * 12)); // ChestProp.Type
            writer.U32(U32(report, 0x464 + i * 12)); // ChestProp.ItemId
            writer.U32(U32(report, 0x468 + i * 12)); // ChestProp.AvailbilityHour
            writer.U16(U16(report, 0x46C + i * 12)); // ChestProp.IsExcellentAward
        }
        writer.U32(U32(report, 0x522)); // ModID
    }
}
