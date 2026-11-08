using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reads PVE/trap statistics for integrated Tower Defense maps without creating players, driving waves, or
/// invoking accumulating OnlineDS reports.
/// </summary>
internal sealed class AfdevTowerDefenseSettlementReader : AfdevSettlementReader
{
    private readonly Dictionary<Guid, PlayerSample> players = [];
    private readonly Dictionary<Guid, MatchSettlementSpectator> spectators = [];

    internal AfdevTowerDefenseSettlementReader(AfdevOwnedProcess process, MatchStartSnapshot start,
        AfdevNativeEvidence world) : base(process, start, world)
    {
        if (!AfdevMapCatalog.MatchesTowerDefense(world) || start.BioBotPlan is not null)
            throw new InvalidDataException("塔防结算必须属于无玩家 Bot 名册的已接入精确地图。");

        CheckCode(0x01477D00, 6375, "13649E932425B68F798B2016A2160104434091DA594F863D1945288CC90F144C");
        CheckCode(0x01474D80, 902, "E8A98977B71AA757CE11EB19149D04FB2CC9594A61C3E0A70D2049CEEE068979");
    }

    internal override MatchSettlementSnapshot Capture(AfdevNativeEvidence world)
    {
        CheckWorld(world);
        if (completed is not null) return completed;

        uint gri = reader.U32(Field(world.GameInfo, "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4, 0x2A8));
        var griIdentity = reader.Identity(gri);

        if (reader.Path(griIdentity.Class) != "ATDGame.ATDGameGameReplicationInfo")
            throw new InvalidDataException("塔防结算 GRI 类型发生变化。");

        Field(gri, "PVEGame.PVEGameReplicationInfo.Difficulty", "ByteProperty", 1, 0x31C);
        Field(gri, "PVEGame.PVEGameReplicationInfo.EndMatchReason", "IntProperty", 4, 0x32C);
        Field(gri, "PVEGame.PVEGameReplicationInfo.UinWhosKillBoss", "StrProperty", 12, 0x49C);
        Field(gri, "PVEGame.ATDGameReplicationInfoBase.CurrentRound", "IntProperty", 4, 0x4AC);
        Field(world.GameInfo, "PVEGame.PVEGame.bMissionSuccess", "BoolProperty", 4, 0x6A0);

        bool ended = IsCompleted(world.GameInfo, gri);
        float worldTime = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(
            Field(world.WorldInfo, "Engine.WorldInfo.TimeSeconds", "FloatProperty", 4, 0x394), 4));
        byte[] game = process.ReadMemory(world.GameInfo, 0x6A4);
        byte[] gameStats = process.ReadMemory(gri, 0x4BC);
        var priArray = ReadArray(gri + 0x210, 4, 64);
        var observed = DateTimeOffset.UtcNow;
        var freshPlayers = new Dictionary<Guid, PlayerSample>();
        var freshSpectators = new Dictionary<Guid, MatchSettlementSpectator>();

        if (!float.IsFinite(worldTime) || worldTime < 0)
            throw new InvalidDataException("塔防世界时间无效。");

        string bossKillerText = ReadString(gameStats.AsSpan(0x49C, 12));

        _ = ulong.TryParse(bossKillerText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong bossKiller);

        byte[] common = PveSettlementStatistics.ReadGame(0x2005, worldTime, game, gameStats, 0, bossKiller);
        ushort gameTime = PveSettlementStatistics.U16(common, 0);
        ushort round = PveSettlementStatistics.U16(gameStats, 0x4AC);

        for (int offset = 0; offset < priArray.Bytes.Length; offset += 4)
        {
            uint pri = U32(priArray.Bytes, offset);

            if (pri < 0x10000) continue;

            var identity = reader.Identity(pri);

            if (!reader.IsA(pri, "ATDGame.ATDGamePlayerReplicationInfo")) continue;

            byte[] values = process.ReadMemory(pri, 0x1118);
            string uinText = ReadUin(pri, values);

            if (!ulong.TryParse(uinText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ||
                !members.TryGetValue(uin, out var member) || Bit(pri, "Engine.PlayerReplicationInfo.bBot") ||
                (U32(values, 0x3C0) & 0x30000) != 0 || retiredSessions.Contains(member.Session.SessionId))
                continue;

            if (reader.U32(Field(pri, "Engine.Actor.WorldInfo", "ObjectProperty", 4, 0xEC)) != world.WorldInfo)
                throw new InvalidDataException("塔防参战 PRI 已离开当前世界。");

            uint controller = reader.U32(Field(pri, "Engine.Actor.Owner", "ObjectProperty", 4));

            if (controller < 0x10000 || !reader.IsA(controller, "ATDGame.ATDGamePlayerController")) continue;

            var controllerIdentity = reader.Identity(controller);
            uint connection = reader.U32(Field(controller, "Engine.PlayerController.Player", "ObjectProperty", 4));

            // Require the native controller of this match's authenticated connection. Monsters, offline PRIs, and objects outside the frozen roster cannot become account statistics.
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
                var statistics = TowerDefenseSettlementStatistics.ReadPlayer(uin, worldTime, gameTime, round, values, arrays);

                freshPlayers.Add(member.Session.SessionId, new PlayerSample(member, statistics));

                foreach (var array in new[] { weapons, cyborgs, pickups, rolled, skills, chests }) CheckArray(array);
            }

            if (reader.Identity(pri) != identity || reader.Identity(controller) != controllerIdentity ||
                !process.ReadMemory(pri + 0xB64, 12).AsSpan().SequenceEqual(values.AsSpan(0xB64, 12)))
                throw new InvalidDataException("塔防结算采样期间玩家对象发生变化。");
        }

        CheckArray(priArray);
        CheckWorld(world);

        if (reader.Identity(gri) != griIdentity || reader.U32(world.GameInfo + 0x2A8) != gri ||
            IsCompleted(world.GameInfo, gri) != ended || reader.U32(gri + 0x4AC) != U32(gameStats, 0x4AC))
            throw new InvalidDataException("塔防结算采样跨越世界、波次或结束状态变化，等待下一次完整样本。");

        foreach (var (session, sample) in freshPlayers) players[session] = sample;
        foreach (var (session, spectator) in freshSpectators) spectators[session] = spectator;

        var ordered = start.Members.Where(member => players.ContainsKey(member.Session.SessionId))
            .Select(member => players[member.Session.SessionId]).ToArray();
        var orderedSpectators = start.Members.Where(member => spectators.ContainsKey(member.Session.SessionId))
            .Select(member => spectators[member.Session.SessionId]).ToImmutableArray();

        PveSettlementStatistics.Put16(common, 4, ordered.Length);

        var writer = TowerDefenseSettlementStatistics.WriteGame(common, gameStats,
            ordered.Select(sample => sample.Statistics).ToArray());

        foreach (var sample in ordered) sample.Statistics.WriteTo(writer);

        TowerDefenseSettlementStatistics.WriteEmptyRankings(writer, world.MapId);

        // Native Money is match-local currency for tower construction and similar uses. Unverified account-reward multipliers cannot justify credits.
        var result = new MatchSettlementSnapshot(start, world.AttemptId, world.WorldGeneration, ended, observed,
            [.. ordered.Select(sample => new MatchSettlementPlayer(sample.Member, 0, 0))],
            orderedSpectators, [.. writer.ToArray()]);

        if (ended) completed = result;

        return result;
    }

    private bool IsCompleted(uint game, uint gri)
    {
        bool gameEnded = Bit(game, "Engine.GameInfo.bGameEnded");
        bool matchOver = Bit(gri, "Engine.GameReplicationInfo.bMatchIsOver");
        bool missionEnded = Bit(gri, "PVEGame.PVEGameReplicationInfo.bGameEnded");

        return gameEnded && matchOver && missionEnded;
    }

    private void CheckPlayerLayout(uint pri)
    {
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.Points", "FloatProperty", 4, 0xEF4);

        uint field = reader.FindField(reader.U32(pri + 0x34), "TrapInfoList",
            "PVEGame.ATDPlayerReplicationInfoBase.TrapInfoList");

        if (reader.Path(reader.U32(field + 0x34)) != "Core.StructProperty" || reader.U32(field + 0x40) != 32 ||
            reader.U32(field + 0x44) != 24 || reader.U32(field + 0x60) != 0xF80 ||
            reader.Path(reader.U32(field + 0x84)) != "PVEGame.ATDPlayerReplicationInfoBase.TrapInfo")
            throw new InvalidDataException("塔防陷阱统计布局与已核验原生报告不一致。");
    }

    private sealed record PlayerSample(MatchStartMember Member, TowerDefenseSettlementStatistics Statistics);
}
