using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reads native TGSV statistics for integrated Survival maps using exact-world/session boundaries, without
/// driving waves or calling score-accumulating reports.
/// </summary>
internal sealed class AfdevSurvivalSettlementReader : AfdevSettlementReader
{
    private readonly Dictionary<Guid, PlayerSample> players = [];
    private readonly Dictionary<Guid, MatchSettlementSpectator> spectators = [];

    internal AfdevSurvivalSettlementReader(AfdevOwnedProcess process, MatchStartSnapshot start,
        AfdevNativeEvidence world) : base(process, start, world)
    {
        if (!AfdevMapCatalog.MatchesSurvival(world) || start.BioBotPlan is not null)
            throw new InvalidDataException("生存战结算必须属于无玩家 Bot 名册且已接入的精确 TGSV 世界。");

        CheckCode(0x01477D00, 6375, "13649E932425B68F798B2016A2160104434091DA594F863D1945288CC90F144C");
        CheckCode(0x01474450, 1715, "CF2A93242A274CB40F426F0D7DFECC0DFB4FEB4516F5407F6311070EDC1236EE");
    }

    internal override MatchSettlementSnapshot Capture(AfdevNativeEvidence world)
    {
        CheckWorld(world);
        if (completed is not null) return completed;

        uint gri = reader.U32(Field(world.GameInfo, "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4, 0x2A8));
        var griIdentity = reader.Identity(gri);

        if (reader.Path(griIdentity.Class) != "PVEGame.TGSVGameReplicationInfo")
            throw new InvalidDataException("生存战结算 GRI 类型与 TGSV 规则不一致。");

        CheckGameLayout(world.GameInfo, gri);

        bool ended = IsCompleted(world.GameInfo, gri);
        float worldTime = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(
            Field(world.WorldInfo, "Engine.WorldInfo.TimeSeconds", "FloatProperty", 4, 0x394), 4));
        byte[] game = process.ReadMemory(world.GameInfo, 0x6A4);
        byte[] gameStats = process.ReadMemory(gri, 0x530);
        var priArray = ReadArray(gri + 0x210, 4, 64);
        var jerricanRounds = ReadArray(gri + 0x4FC, 4, 4096, SurvivalSettlementStatistics.MaximumRounds);
        var autoGunRounds = ReadArray(gri + 0x510, 4, 4096, SurvivalSettlementStatistics.MaximumRounds);
        var observed = DateTimeOffset.UtcNow;
        var freshPlayers = new Dictionary<Guid, PlayerSample>();
        var freshSpectators = new Dictionary<Guid, MatchSettlementSpectator>();

        if (!float.IsFinite(worldTime) || worldTime < 0 || U32(gameStats, 0x4B8) > SurvivalSettlementStatistics.MaximumRounds)
            throw new InvalidDataException("生存战世界时间或波次无效。");

        string bossKillerText = ReadString(gameStats.AsSpan(0x49C, 12));

        _ = ulong.TryParse(bossKillerText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong bossKiller);

        byte[] common = PveSettlementStatistics.ReadGame(0x2001, worldTime, game, gameStats, 0, bossKiller);
        ushort gameTime = PveSettlementStatistics.U16(common, 0);
        ushort round = PveSettlementStatistics.U16(gameStats, 0x4B8);

        for (int offset = 0; offset < priArray.Bytes.Length; offset += 4)
        {
            uint pri = U32(priArray.Bytes, offset);

            if (pri < 0x10000 || !reader.IsA(pri, "PVEGame.TGSVPlayerReplicationInfo")) continue;

            var identity = reader.Identity(pri);
            byte[] values = process.ReadMemory(pri, 0xFB4);
            string uinText = ReadUin(pri, values);

            if (!ulong.TryParse(uinText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ||
                !members.TryGetValue(uin, out var member) || Bit(pri, "Engine.PlayerReplicationInfo.bBot") ||
                (U32(values, 0x3C0) & 0x30000) != 0 || retiredSessions.Contains(member.Session.SessionId))
                continue;

            if (reader.U32(Field(pri, "Engine.Actor.WorldInfo", "ObjectProperty", 4, 0xEC)) != world.WorldInfo)
                throw new InvalidDataException("生存战参战 PRI 已离开当前世界。");

            uint controller = reader.U32(Field(pri, "Engine.Actor.Owner", "ObjectProperty", 4));

            if (controller < 0x10000 || !reader.IsA(controller, "PVEGame.TGSVPlayerController")) continue;

            var controllerIdentity = reader.Identity(controller);
            uint connection = reader.U32(Field(controller, "Engine.PlayerController.Player", "ObjectProperty", 4));

            // Accept only actual native connections for this round's frozen identities. Level monsters and old PRIs cannot become settlement accounts.
            if (connection < 0x10000 || reader.U32(connection + 0x60) != world.NetDriver ||
                reader.U32(connection + 0x40) != controller ||
                reader.U32(Field(controller, "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4)) != pri ||
                ((U32(values, 0x3C0) & 4) != 0) != member.Observer)
                continue;

            if (member.Observer)
            {
                freshSpectators.Add(member.Session.SessionId, new MatchSettlementSpectator(member, values[0xB24]));
            }
            else
            {
                CheckPlayerLayout(pri);

                var weapons = ReadArray(pri + 0xF08, 56, 4096, 16);
                var cyborgs = ReadArray(pri + 0xEC4, 12, 4096, 16);
                var pickups = ReadArray(pri + 0xED0, 8, 4096, 10);
                var rolled = ReadArray(pri + 0xEDC, 16, 4096, 32);
                var skills = ReadArray(pri + 0xEE8, 4, 4096, 5);
                var chests = ReadArray(pri + 0xF64, 16, 4096, 16);
                var arrays = new PveSettlementArrays(weapons.Bytes, cyborgs.Bytes, pickups.Bytes,
                    rolled.Bytes, skills.Bytes, chests.Bytes);
                var statistics = SurvivalSettlementStatistics.ReadPlayer(uin, worldTime, gameTime, round, values, arrays);

                freshPlayers.Add(member.Session.SessionId, new PlayerSample(member, statistics));

                foreach (var array in new[] { weapons, cyborgs, pickups, rolled, skills, chests }) CheckArray(array);
            }

            if (reader.Identity(pri) != identity || reader.Identity(controller) != controllerIdentity ||
                !process.ReadMemory(pri + 0xB64, 12).AsSpan().SequenceEqual(values.AsSpan(0xB64, 12)))
                throw new InvalidDataException("生存战结算采样期间玩家对象发生变化。");
        }

        foreach (var array in new[] { priArray, jerricanRounds, autoGunRounds }) CheckArray(array);

        CheckWorld(world);

        if (reader.Identity(gri) != griIdentity || reader.U32(world.GameInfo + 0x2A8) != gri ||
            IsCompleted(world.GameInfo, gri) != ended || reader.U32(gri + 0x4B8) != U32(gameStats, 0x4B8))
            throw new InvalidDataException("生存战结算采样跨越世界、波次或结束状态变化，等待下一次完整样本。");

        foreach (var (session, sample) in freshPlayers) players[session] = sample;
        foreach (var (session, spectator) in freshSpectators) spectators[session] = spectator;

        var ordered = start.Members.Where(member => players.ContainsKey(member.Session.SessionId))
            .Select(member => players[member.Session.SessionId]).ToArray();
        var orderedSpectators = start.Members.Where(member => spectators.ContainsKey(member.Session.SessionId))
            .Select(member => spectators[member.Session.SessionId]).ToImmutableArray();
        var writer = SurvivalSettlementStatistics.WriteGame(common, gameStats, jerricanRounds.Bytes, autoGunRounds.Bytes,
            ordered.Select(sample => sample.Statistics).ToArray());

        foreach (var sample in ordered) sample.Statistics.WriteTo(writer);

        SurvivalSettlementStatistics.WriteEmptyRankings(writer, world.MapId);

        // Window-repair/machine-gun Money is match-local, not account gp. Rewards retain authoritative sources and atomic receipt boundaries.
        var result = new MatchSettlementSnapshot(start, world.AttemptId, world.WorldGeneration, ended, observed,
            [.. ordered.Select(sample => new MatchSettlementPlayer(sample.Member, 0, 0))],
            orderedSpectators, [.. writer.ToArray()]);

        if (ended) completed = result;

        return result;
    }

    private bool IsCompleted(uint game, uint gri)
        => Bit(game, "Engine.GameInfo.bGameEnded") && Bit(gri, "Engine.GameReplicationInfo.bMatchIsOver") &&
            Bit(gri, "PVEGame.PVEGameReplicationInfo.bGameEnded");

    private void CheckGameLayout(uint game, uint gri)
    {
        Field(game, "PVEGame.PVEGame.bMissionSuccess", "BoolProperty", 4, 0x6A0);
        Field(gri, "PVEGame.PVEGameReplicationInfo.Difficulty", "ByteProperty", 1, 0x31C);
        Field(gri, "PVEGame.PVEGameReplicationInfo.EndMatchReason", "IntProperty", 4, 0x32C);
        Field(gri, "PVEGame.PVEGameReplicationInfo.UinWhosKillBoss", "StrProperty", 12, 0x49C);
        Field(gri, "PVEGame.TGSVGameReplicationInfo.CurrentRound", "IntProperty", 4, 0x4B8);
        Field(gri, "PVEGame.TGSVGameReplicationInfo.BigMonsterHealthMax", "FloatProperty", 4, 0x4EC);

        (string Name, uint Offset)[] integers =
        [
            ("KillBigMonsterUpTime", 0x4F4), ("JerricanBombCount", 0x4F8),
            ("MonsterKilledByJerricanCount", 0x508), ("AutoGunUsedCount", 0x50C),
            ("MonsterKilledByAutoGunCount", 0x51C), ("BMBeginPeopleCount", 0x520),
            ("BMOverPeopleCount", 0x524), ("BMOverHealth", 0x528), ("BMOverPeriod", 0x52C)
        ];

        foreach (var field in integers)
            Field(gri, "PVEGame.TGSVGameReplicationInfo." + field.Name, "IntProperty", 4, field.Offset);

        CheckRoundArray(gri, "JerricanBombCountArray", 0x4FC);
        CheckRoundArray(gri, "AutoGunUsedCountArray", 0x510);
    }

    private void CheckRoundArray(uint gri, string name, uint offset)
    {
        string path = "PVEGame.TGSVGameReplicationInfo." + name;
        uint field = reader.FindField(reader.Identity(gri).Class, name, path);
        uint inner = reader.U32(field + 0x84);

        Field(gri, path, "ArrayProperty", 12, offset);

        if (reader.Path(reader.Identity(inner).Class) != "Core.IntProperty" || reader.U32(inner + 0x44) != 4)
            throw new InvalidDataException("生存战逐波统计必须是原生 IntProperty 动态数组。");
    }

    private void CheckPlayerLayout(uint pri)
    {
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.Points", "FloatProperty", 4, 0xEF4);
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.SpecialKillCount", "IntProperty", 4, 0xEAC);
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.SpecialKillPoints", "IntProperty", 4, 0xEB0);
        Field(pri, "PVEGame.TGSVPlayerReplicationInfo.TimesCyborgTerminator", "IntProperty", 4, 0xF98);
        Field(pri, "PVEGame.TGSVPlayerReplicationInfo.RoundAtFirstTimeWhenWindowDestroyed", "IntProperty", 4, 0xFA0);
        Field(pri, "PVEGame.TGSVPlayerReplicationInfo.RepairWindowTimes", "IntProperty", 4, 0xFA4);
        Field(pri, "PVEGame.TGSVPlayerReplicationInfo.BossDamage", "IntProperty", 4, 0xFA8);
        Field(pri, "PVEGame.TGSVPlayerReplicationInfo.BossBeKilled", "IntProperty", 4, 0xFAC);
        Field(pri, "PVEGame.TGSVPlayerReplicationInfo.BossKill", "IntProperty", 4, 0xFB0);
    }

    private sealed record PlayerSample(MatchStartMember Member, SurvivalSettlementStatistics Statistics);
}
