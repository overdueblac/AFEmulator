using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reads shared PVE and map-specific statistics for both Defense maps. Selects IF/IF2 by exact map without
/// driving levels or accumulating native reports.
/// </summary>
internal sealed class AfdevDefenseSettlementReader : AfdevSettlementReader
{
    private readonly bool starship;
    private readonly Dictionary<Guid, PlayerSample> players = [];
    private readonly Dictionary<Guid, MatchSettlementSpectator> spectators = [];

    internal AfdevDefenseSettlementReader(AfdevOwnedProcess process, MatchStartSnapshot start,
        AfdevNativeEvidence world) : base(process, start, world)
    {
        if (!AfdevMapCatalog.MatchesDefense(world) || start.BioBotPlan is not null)
            throw new InvalidDataException("保卫战结算必须属于无玩家 Bot 名册的已接入精确世界。");

        starship = world.MapId == 0x005F;

        CheckCode(0x01477D00, 6375, "13649E932425B68F798B2016A2160104434091DA594F863D1945288CC90F144C");

        if (starship)
            CheckCode(0x01475830, 1669, "A554FC9D2DB1FEB3B2A4B1CF568DA60FEC7AA3168C4C1DCD7F46C8E7C61057B7");
        else
            CheckCode(0x01475110, 1817, "E731435BAE4F0E32BACD17041E9B996CFEF7A74BBEF48D831A5C7996341E2B2F");
    }

    internal override MatchSettlementSnapshot Capture(AfdevNativeEvidence world)
    {
        CheckWorld(world);
        if (completed is not null) return completed;

        uint gri = reader.U32(Field(world.GameInfo, "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4, 0x2A8));
        var griIdentity = reader.Identity(gri);
        string griClass = starship ? "TGIFGame.TGIF2GameReplicationInfo" : "PVEGame.TGIFGameReplicationInfo";
        string priClass = starship ? "TGIFGame.TGIF2PlayerReplicationInfo" : "PVEGame.TGIFPlayerReplicationInfo";
        string controllerClass = starship ? "TGIFGame.TGIF2PlayerController" : "PVEGame.TGIFPlayerController";
        int roundOffset = starship ? 0x4B8 : 0x4CC;

        if (reader.Path(griIdentity.Class) != griClass)
            throw new InvalidDataException("保卫战结算 GRI 类型与精确地图不一致。");

        Field(gri, "PVEGame.PVEGameReplicationInfo.Difficulty", "ByteProperty", 1, 0x31C);
        Field(gri, "PVEGame.PVEGameReplicationInfo.EndMatchReason", "IntProperty", 4, 0x32C);
        Field(gri, "PVEGame.PVEGameReplicationInfo.UinWhosKillBoss", "StrProperty", 12, 0x49C);
        Field(world.GameInfo, "PVEGame.PVEGame.bMissionSuccess", "BoolProperty", 4, 0x6A0);
        CheckGameLayout(gri);

        bool ended = IsCompleted(world.GameInfo, gri);
        float worldTime = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(
            Field(world.WorldInfo, "Engine.WorldInfo.TimeSeconds", "FloatProperty", 4, 0x394), 4));
        byte[] game = process.ReadMemory(world.GameInfo, 0x6A4);
        byte[] gameStats = process.ReadMemory(gri, starship ? 0x4E4 : 0x578);
        var priArray = ReadArray(gri + 0x210, 4, 64);
        var observed = DateTimeOffset.UtcNow;
        var freshPlayers = new Dictionary<Guid, PlayerSample>();
        var freshSpectators = new Dictionary<Guid, MatchSettlementSpectator>();

        if (!float.IsFinite(worldTime) || worldTime < 0)
            throw new InvalidDataException("保卫战世界时间无效。");

        string bossKillerText = ReadString(gameStats.AsSpan(0x49C, 12));

        _ = ulong.TryParse(bossKillerText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong bossKiller);

        byte[] common = PveSettlementStatistics.ReadGame(0x2002, worldTime, game, gameStats, 0, bossKiller);
        ushort gameTime = PveSettlementStatistics.U16(common, 0);
        ushort round = PveSettlementStatistics.U16(gameStats, roundOffset);

        for (int offset = 0; offset < priArray.Bytes.Length; offset += 4)
        {
            uint pri = U32(priArray.Bytes, offset);

            if (pri < 0x10000) continue;

            var identity = reader.Identity(pri);

            if (!reader.IsA(pri, priClass)) continue;

            byte[] values = process.ReadMemory(pri, starship ? 0x1000 : 0xF84);
            string uinText = ReadUin(pri, values);

            if (!ulong.TryParse(uinText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ||
                !members.TryGetValue(uin, out var member) || Bit(pri, "Engine.PlayerReplicationInfo.bBot") ||
                (U32(values, 0x3C0) & 0x30000) != 0 || retiredSessions.Contains(member.Session.SessionId))
                continue;

            if (reader.U32(Field(pri, "Engine.Actor.WorldInfo", "ObjectProperty", 4, 0xEC)) != world.WorldInfo)
                throw new InvalidDataException("保卫战参战 PRI 已离开当前世界。");

            uint controller = reader.U32(Field(pri, "Engine.Actor.Owner", "ObjectProperty", 4));

            if (controller < 0x10000 || !reader.IsA(controller, controllerClass)) continue;

            var controllerIdentity = reader.Identity(controller);
            uint connection = reader.U32(Field(controller, "Engine.PlayerController.Player", "ObjectProperty", 4));

            // Statistics belong only to actual native connections for frozen sessions. Level monsters, offline PRIs, and other worlds cannot enter account settlement.
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
                var statistics = starship
                    ? DefenseSettlementStatistics.ReadStarshipPlayer(uin, worldTime, gameTime, round, values, arrays)
                    : DefenseSettlementStatistics.ReadPlayer(uin, worldTime, gameTime, round, values, arrays);

                freshPlayers.Add(member.Session.SessionId, new PlayerSample(member, statistics));

                foreach (var array in new[] { weapons, cyborgs, pickups, rolled, skills, chests }) CheckArray(array);
            }

            if (reader.Identity(pri) != identity || reader.Identity(controller) != controllerIdentity ||
                !process.ReadMemory(pri + 0xB64, 12).AsSpan().SequenceEqual(values.AsSpan(0xB64, 12)))
                throw new InvalidDataException("保卫战结算采样期间玩家对象发生变化。");
        }

        CheckArray(priArray);
        CheckWorld(world);

        if (reader.Identity(gri) != griIdentity || reader.U32(world.GameInfo + 0x2A8) != gri ||
            IsCompleted(world.GameInfo, gri) != ended || reader.U32(gri + (uint)roundOffset) != U32(gameStats, roundOffset) ||
            (starship && reader.U32(gri + 0x4BC) != U32(gameStats, 0x4BC)))
            throw new InvalidDataException("保卫战结算采样跨越世界、阶段或结束状态变化，等待下一次完整样本。");

        foreach (var (session, sample) in freshPlayers) players[session] = sample;
        foreach (var (session, spectator) in freshSpectators) spectators[session] = spectator;

        var ordered = start.Members.Where(member => players.ContainsKey(member.Session.SessionId))
            .Select(member => players[member.Session.SessionId]).ToArray();
        var orderedSpectators = start.Members.Where(member => spectators.ContainsKey(member.Session.SessionId))
            .Select(member => spectators[member.Session.SessionId]).ToImmutableArray();

        PveSettlementStatistics.Put16(common, 4, ordered.Length);

        var statisticsList = ordered.Select(sample => sample.Statistics).ToArray();
        var writer = starship
            ? DefenseSettlementStatistics.WriteStarshipGame(common, gameStats, worldTime, statisticsList)
            : DefenseSettlementStatistics.WriteGame(common, gameStats, statisticsList);

        foreach (var sample in ordered) sample.Statistics.WriteTo(writer);

        DefenseSettlementStatistics.WriteEmptyRankings(writer, world.MapId);

        // Match-local mech/turret Money is not account gp. Without authoritative rewards, do not invent coins or experience.
        var result = new MatchSettlementSnapshot(start, world.AttemptId, world.WorldGeneration, ended, observed,
            [.. ordered.Select(sample => new MatchSettlementPlayer(sample.Member, 0, 0))],
            orderedSpectators, [.. writer.ToArray()]);

        if (ended) completed = result;

        return result;
    }

    private bool IsCompleted(uint game, uint gri)
        => Bit(game, "Engine.GameInfo.bGameEnded") && Bit(gri, "Engine.GameReplicationInfo.bMatchIsOver") &&
            Bit(gri, "PVEGame.PVEGameReplicationInfo.bGameEnded");

    private void CheckPlayerLayout(uint pri)
    {
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.Points", "FloatProperty", 4, 0xEF4);
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.SpecialKillCount", "IntProperty", 4, 0xEAC);
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.SpecialKillPoints", "IntProperty", 4, 0xEB0);

        if (starship)
        {
            const string owner = "PVEGame.TGIF2PlayerReplicationInfoBase.";

            Field(pri, owner + "TrapSelectUsedCount", "IntProperty", 4, 0xF70);
            Field(pri, owner + "BossDamageAmount", "IntProperty", 4, 0xFF4);
            Field(pri, owner + "BossPlayerDiedCount", "IntProperty", 4, 0xFF8);
            Field(pri, owner + "BossWeakPointDestroyedCount", "IntProperty", 4, 0xFFC);
            CheckStarshipArray(pri, "TrapInfoList", "IF2TrapInfo", 0xF74, 12);
            CheckStarshipArray(pri, "MechaInfoList", "IF2MechaInfo", 0xFA4, 20);
        }
        else
        {
            Field(pri, "PVEGame.TGIFPlayerReplicationInfo.BossDamage", "IntProperty", 4, 0xF78);
            Field(pri, "PVEGame.TGIFPlayerReplicationInfo.BossBeKilled", "IntProperty", 4, 0xF7C);
            Field(pri, "PVEGame.TGIFPlayerReplicationInfo.BossTurret", "IntProperty", 4, 0xF80);
        }
    }

    private void CheckGameLayout(uint gri)
    {
        if (starship)
        {
            const string owner = "PVEGame.TGIF2GameReplicationInfoBase.";

            Field(gri, owner + "CurrentRound", "IntProperty", 4, 0x4B8);
            Field(gri, owner + "GameStartTime", "FloatProperty", 4, 0x4BC);
            Field(gri, owner + "PlayerCountBegin", "IntProperty", 4, 0x4C0);
            Field(gri, owner + "PlayerCount5Min", "IntProperty", 4, 0x4C4);
            Field(gri, owner + "PlayerCount10Min", "IntProperty", 4, 0x4C8);
            Field(gri, owner + "Level1Round1SuccessBaseBloodPercent", "FloatProperty", 4, 0x4CC);
            Field(gri, owner + "Level1Round2SuccessBaseBloodPercent", "FloatProperty", 4, 0x4D0);
            Field(gri, owner + "BossPlayerCount", "IntProperty", 4, 0x4D4);
            Field(gri, owner + "TimeLeftWhenSuccess", "IntProperty", 4, 0x4DC);
            Field(gri, owner + "BossHealthLeftWhenFail", "IntProperty", 4, 0x4E0);
        }
        else
        {
            Field(gri, "PVEGame.TGIFGameReplicationInfo.CurrentRound", "IntProperty", 4, 0x4CC);
            Field(gri, "PVEGame.TGIFGameReplicationInfo.RoundTime", "FloatProperty", 4, 0x4D4);
            Field(gri, "PVEGame.TGIFGameReplicationInfo.FirstBaseLowestHealth", "IntProperty", 4, 0x544);
            Field(gri, "PVEGame.TGIFGameReplicationInfo.Level2SuccessBaseBloodPercent", "FloatProperty", 4, 0x574);
        }
    }

    private void CheckStarshipArray(uint pri, string name, string structureName, uint offset, uint stride)
    {
        const string owner = "PVEGame.TGIF2PlayerReplicationInfoBase.";
        uint type = reader.Identity(pri).Class;
        uint field = reader.FindField(type, name, owner + name);

        if (reader.Path(reader.U32(field + 0x34)) != "Core.StructProperty" || reader.U32(field + 0x40) != 4 ||
            reader.U32(field + 0x44) != stride || reader.U32(field + 0x60) != offset ||
            offset + 4 * stride > reader.U32(type + 0x50) ||
            reader.Path(reader.U32(field + 0x84)) != owner + structureName)
            throw new InvalidDataException("星海虫影机甲或陷阱固定数组布局变化：" + name);
    }

    private sealed record PlayerSample(MatchStartMember Member, DefenseSettlementStatistics Statistics);
}
