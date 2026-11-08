using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reads LD native statistics for Hideout using exact-world, session, and frozen-departure boundaries.
/// Never calls score-accumulating reports.
/// </summary>
internal sealed class AfdevEncirclementSettlementReader : AfdevSettlementReader
{
    private readonly Dictionary<Guid, PlayerSample> players = [];
    private readonly Dictionary<Guid, MatchSettlementSpectator> spectators = [];

    internal AfdevEncirclementSettlementReader(AfdevOwnedProcess process, MatchStartSnapshot start,
        AfdevNativeEvidence world) : base(process, start, world)
    {
        if (!AfdevMapCatalog.MatchesEncirclement(world) || start.BioBotPlan is not null)
            throw new InvalidDataException("围剿战结算必须属于无玩家 Bot 名册的藏匿之地 LD 世界。");

        CheckCode(0x01477D00, 6375, "13649E932425B68F798B2016A2160104434091DA594F863D1945288CC90F144C");
        CheckCode(0x01474B10, 623, "117A6A4C0B68D75569B7D4CBA057E273BA6906905EE7255D692157B15CB8992A");
    }

    internal override MatchSettlementSnapshot Capture(AfdevNativeEvidence world)
    {
        CheckWorld(world);
        if (completed is not null) return completed;

        uint gri = reader.U32(Field(world.GameInfo, "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4, 0x2A8));
        var griIdentity = reader.Identity(gri);

        if (reader.Path(griIdentity.Class) != "LDGame.TGLDGameReplicationInfo")
            throw new InvalidDataException("围剿战结算 GRI 类型与 LD 规则不一致。");

        CheckGameLayout(world.GameInfo, gri);

        bool ended = IsCompleted(world.GameInfo, gri);
        float worldTime = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(
            Field(world.WorldInfo, "Engine.WorldInfo.TimeSeconds", "FloatProperty", 4, 0x394), 4));
        byte[] game = process.ReadMemory(world.GameInfo, 0x6A4);
        byte[] gameStats = process.ReadMemory(gri, 0x4B8);
        var priArray = ReadArray(gri + 0x210, 4, 64);
        var observed = DateTimeOffset.UtcNow;
        var freshPlayers = new Dictionary<Guid, PlayerSample>();
        var freshSpectators = new Dictionary<Guid, MatchSettlementSpectator>();

        if (!float.IsFinite(worldTime) || worldTime < 0 || U32(gameStats, 0x4AC) > EncirclementSettlementStatistics.MaximumRounds)
            throw new InvalidDataException("围剿战世界时间或关卡数无效。");

        string bossKillerText = ReadString(gameStats.AsSpan(0x49C, 12));

        _ = ulong.TryParse(bossKillerText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong bossKiller);

        byte[] common = PveSettlementStatistics.ReadGame(0x2003, worldTime, game, gameStats, 0, bossKiller);
        ushort gameTime = PveSettlementStatistics.U16(common, 0);
        ushort round = PveSettlementStatistics.U16(gameStats, 0x4AC);

        for (int offset = 0; offset < priArray.Bytes.Length; offset += 4)
        {
            uint pri = U32(priArray.Bytes, offset);

            if (pri < 0x10000 || !reader.IsA(pri, "LDGame.TGLDPlayerReplicationInfo")) continue;

            var identity = reader.Identity(pri);
            byte[] values = process.ReadMemory(pri, 0xF88);
            string uinText = ReadUin(pri, values);

            if (!ulong.TryParse(uinText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ||
                !members.TryGetValue(uin, out var member) || Bit(pri, "Engine.PlayerReplicationInfo.bBot") ||
                (U32(values, 0x3C0) & 0x30000) != 0 || retiredSessions.Contains(member.Session.SessionId))
                continue;

            if (reader.U32(Field(pri, "Engine.Actor.WorldInfo", "ObjectProperty", 4, 0xEC)) != world.WorldInfo)
                throw new InvalidDataException("围剿战参战 PRI 已离开当前世界。");

            uint controller = reader.U32(Field(pri, "Engine.Actor.Owner", "ObjectProperty", 4));

            if (controller < 0x10000 || !reader.IsA(controller, "LDGame.TGLDPlayerController")) continue;

            var controllerIdentity = reader.Identity(controller);
            uint connection = reader.U32(Field(controller, "Engine.PlayerController.Player", "ObjectProperty", 4));

            // Accept only actual same-match connections for frozen identities. Level enemies, old PRIs, and observers cannot join participant statistics.
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
                var statistics = EncirclementSettlementStatistics.ReadPlayer(uin, worldTime, gameTime, round, values, arrays);

                freshPlayers.Add(member.Session.SessionId, new PlayerSample(member, statistics));

                foreach (var array in new[] { weapons, cyborgs, pickups, rolled, skills, chests }) CheckArray(array);
            }

            if (reader.Identity(pri) != identity || reader.Identity(controller) != controllerIdentity ||
                !process.ReadMemory(pri + 0xB64, 12).AsSpan().SequenceEqual(values.AsSpan(0xB64, 12)))
                throw new InvalidDataException("围剿战结算采样期间玩家对象发生变化。");
        }

        CheckArray(priArray);
        CheckWorld(world);

        if (reader.Identity(gri) != griIdentity || reader.U32(world.GameInfo + 0x2A8) != gri ||
            IsCompleted(world.GameInfo, gri) != ended || reader.U32(gri + 0x4AC) != U32(gameStats, 0x4AC) ||
            Bit(gri, "PVEGame.TGLDGameReplicationInfoBase.bBossBegun") != ((gameStats[0x4B0] & 1) != 0))
            throw new InvalidDataException("围剿战结算采样跨越世界、关卡或结束状态变化，等待下一次完整样本。");

        foreach (var (session, sample) in freshPlayers) players[session] = sample;
        foreach (var (session, spectator) in freshSpectators) spectators[session] = spectator;

        var ordered = start.Members.Where(member => players.ContainsKey(member.Session.SessionId))
            .Select(member => players[member.Session.SessionId]).ToArray();
        var orderedSpectators = start.Members.Where(member => spectators.ContainsKey(member.Session.SessionId))
            .Select(member => spectators[member.Session.SessionId]).ToImmutableArray();
        var writer = EncirclementSettlementStatistics.WriteGame(common, gameStats, ordered.Length);

        foreach (var sample in ordered) sample.Statistics.WriteTo(writer);

        EncirclementSettlementStatistics.WriteEmptyRankings(writer, world.MapId);

        // PVE match-local Money does not convert to account gp. Existing authoritative sources and atomic settlement receipts handle rewards.
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
        Field(gri, "PVEGame.TGLDGameReplicationInfoBase.CurrentRound", "IntProperty", 4, 0x4AC);
        Field(gri, "PVEGame.TGLDGameReplicationInfoBase.bBossBegun", "BoolProperty", 4, 0x4B0);
        Field(gri, "PVEGame.TGLDGameReplicationInfoBase.BossRoundTimeLeft", "FloatProperty", 4, 0x4B4);

        uint bossFlag = reader.FindField(reader.Identity(gri).Class, "bBossBegun",
            "PVEGame.TGLDGameReplicationInfoBase.bBossBegun");

        if (reader.U32(bossFlag + 0x84) != 1)
            throw new InvalidDataException("围剿战 Boss 开始标记的原生位布局不一致。");
    }

    private void CheckPlayerLayout(uint pri)
    {
        Field(pri, "PVEGame.PVEPlayerReplicationInfo.Points", "FloatProperty", 4, 0xEF4);
        Field(pri, "PVEGame.TGLDPlayerReplicationInfoBase.LadenDamage", "IntProperty", 4, 0xF70);
        Field(pri, "PVEGame.TGLDPlayerReplicationInfoBase.LadenTumbleCount", "IntProperty", 4, 0xF74);
        Field(pri, "PVEGame.TGLDPlayerReplicationInfoBase.KillRocketerNum", "IntProperty", 4, 0xF7C);
        Field(pri, "PVEGame.TGLDPlayerReplicationInfoBase.KillTerroristNum", "IntProperty", 4, 0xF80);
        Field(pri, "PVEGame.TGLDPlayerReplicationInfoBase.KillTerroristBomberWithThrowWeaponNum", "IntProperty", 4, 0xF84);
    }

    private sealed record PlayerSample(MatchStartMember Member, EncirclementSettlementStatistics Statistics);
}
