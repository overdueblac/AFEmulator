using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reads current ordinary-PvP world and frozen-human statistics from native PVP reports and this build's
/// TDR, without invoking accumulating reports.
/// </summary>
internal sealed class AfdevPvpSettlementReader : AfdevSettlementReader
{
    private readonly Dictionary<Guid, PlayerSample> players = [];
    private readonly string prefix;
    private readonly bool teamMode;
    private readonly bool bomb;
    private readonly Dictionary<Guid, MatchSettlementSpectator> spectators = [];

    internal AfdevPvpSettlementReader(AfdevOwnedProcess process, MatchStartSnapshot start, AfdevNativeEvidence world)
        : base(process, start, world)
    {
        if (!AfdevMapCatalog.MatchesPvp(world))
            throw new InvalidDataException("PvP 结算世界不属于已接入范围。");

        prefix = world.ModeId switch { 0x1001 => "BM", 0x1004 => "IM", _ => "TM" };
        teamMode = AfdevMapCatalog.UsesTeamSeats(world.ModeId);
        bomb = world.ModeId == 0x1001;

        CheckCode(0x0148FAA0, 0x17A2, "475DC74ACD8FFF9082DB0600FC0862CDD107B3F780F3156B0287E52DF8350F0A");
        if (world.ModeId == 0x1002) CheckCode(0x014661B0, 0x3A0, "8F361AC7DEDBEB164C04C9409A1F3B6AC3A482BA430C9F97848642D1F7A15E31");
        if (world.ModeId == 0x1002) CheckCode(0x01465B90, 0x620, "254AB20B37BB5E207B458EF16DB3FAFE2FBEFF514710C6BEACA67C03DD9108B6");
    }

    internal override MatchSettlementSnapshot Capture(AfdevNativeEvidence world)
    {
        CheckWorld(world);
        if (completed is not null) return completed;

        uint gri = reader.U32(Field(world.GameInfo, "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4, 0x2A8));
        var griIdentity = reader.Identity(gri);

        if (!reader.IsA(gri, "UTGame.TG" + prefix + "GameReplicationInfo"))
            throw new InvalidDataException("结算的 GRI 不是本局 PvP 对象。");

        bool ended = IsCompleted(world.GameInfo, gri);
        float worldTime = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(
            Field(world.WorldInfo, "Engine.WorldInfo.TimeSeconds", "FloatProperty", 4, 0x394), 4));
        byte[] game = process.ReadMemory(world.GameInfo, 0x650);
        byte[] gameStats = process.ReadMemory(gri, 0x368);
        var priArray = ReadArray(gri + 0x210, 4, 64);
        var observed = DateTimeOffset.UtcNow;
        var freshPlayers = new Dictionary<Guid, PlayerSample>();
        var freshSpectators = new Dictionary<Guid, MatchSettlementSpectator>();

        if (!float.IsFinite(worldTime) || worldTime < 0)
            throw new InvalidDataException("原生对局时间无效。");

        for (int offset = 0; offset < priArray.Bytes.Length; offset += 4)
        {
            uint pri = U32(priArray.Bytes, offset);

            if (pri < 0x10000) continue;

            var identity = reader.Identity(pri);

            if (!reader.IsA(pri, "UTGame.TG" + prefix + "PlayerReplicationInfo")) continue;

            byte[] values = ReadPlayerValues(pri);
            string uinText = ReadUin(pri, values);

            // Native bots use short numeric UINs. Exclude bBot first and accept only frozen humans with exact connections.
            if (!ulong.TryParse(uinText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ||
                !members.TryGetValue(uin, out var member) || Bit(pri, "Engine.PlayerReplicationInfo.bBot") ||
                (U32(values, 0x3C0) & 0x30000) != 0)
                continue;

            if (retiredSessions.Contains(member.Session.SessionId)) continue;

            if (reader.U32(Field(pri, "Engine.Actor.WorldInfo", "ObjectProperty", 4, 0xEC)) != world.WorldInfo)
                throw new InvalidDataException("参战 PRI 已不属于当前世界。");

            uint controller = reader.U32(Field(pri, "Engine.Actor.Owner", "ObjectProperty", 4));

            // Removed PRIs retain only previously validated samples. Actual connection ownership is required for initial registration or reward updates.
            if (controller < 0x10000 || !reader.IsA(controller, "UTGame.TG" + prefix + "PlayerController")) continue;

            var controllerIdentity = reader.Identity(controller);
            uint connection = reader.U32(Field(controller, "Engine.PlayerController.Player", "ObjectProperty", 4));

            if (connection < 0x10000 || reader.U32(connection + 0x60) != world.NetDriver ||
                reader.U32(connection + 0x40) != controller ||
                reader.U32(Field(controller, "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4)) != pri)
                continue;

            byte camp = !teamMode ? (byte)0 : member.Observer ? values[0xB24] : ReadCamp(U32(values, 0x204));

            if (member.Observer)
            {
                freshSpectators.Add(member.Session.SessionId, new MatchSettlementSpectator(member, camp));
            }
            else
            {
                if (teamMode && camp != TeamDeathmatchSeats.Camp(member.SeatIndex))
                    throw new InvalidDataException("团队战真人阵营与冻结席位不一致。");

                var statistics = PvpStatistics.ReadPlayer(uin, camp, worldTime, values, world.ModeId == 0x1002, ReadModeWords(pri));

                freshPlayers.Add(member.Session.SessionId, new PlayerSample(member, statistics, ended));
            }

            if (reader.Identity(pri) != identity || reader.Identity(controller) != controllerIdentity ||
                !process.ReadMemory(pri + 0xB64, 12).AsSpan().SequenceEqual(values.AsSpan(0xB64, 12)))
                throw new InvalidDataException("结算采样期间玩家对象发生变化。");
        }

        CheckArray(priArray);
        CheckWorld(world);

        if (reader.Identity(gri) != griIdentity || reader.U32(world.GameInfo + 0x2A8) != gri ||
            IsCompleted(world.GameInfo, gri) != ended)
            throw new InvalidDataException("结算采样跨越原生世界或结束状态变化，等待下一次完整样本。");

        foreach (var (session, sample) in freshPlayers) players[session] = sample;
        foreach (var (session, spectator) in freshSpectators) spectators[session] = spectator;

        var ordered = start.Members.Where(member => players.ContainsKey(member.Session.SessionId))
            .Select(member => players[member.Session.SessionId]).ToArray();
        var orderedSpectators = start.Members.Where(member => spectators.ContainsKey(member.Session.SessionId))
            .Select(member => spectators[member.Session.SessionId]).ToImmutableArray();
        var writer = PvpStatistics.WriteGame(game, gameStats, ordered.Length,
            teamMode ? ReadWinningCamp(U32(gameStats, 0x20C)) : (byte)2,
            teamMode ? ReadTeamScores(gri) : [0, 0], ReadAce(gri), bomb ? ReadRounds(gri) : []);

        foreach (var sample in ordered) sample.Statistics.WriteTo(writer);

        var result = new MatchSettlementSnapshot(start, world.AttemptId, world.WorldGeneration, ended, observed,
            [.. ordered.Select(sample => new MatchSettlementPlayer(sample.Member, sample.Statistics.Camp,
                0))], orderedSpectators, [.. writer.ToArray()]);

        // Final match-end samples stop increasing with departure/observation time. Individual round end does not satisfy both native flags.
        if (ended) completed = result;

        return result;
    }

    private byte[] ReadPlayerValues(uint pri)
    {
        // Mode-specific PRI layouts differ after the shared TGPVP prefix. Never read TM extensions as IM/BM achievements.
        var values = new byte[0xF60];

        process.ReadMemory(pri, prefix == "TM" ? 0xF60 : 0xF30).CopyTo(values, 0);

        return values;
    }

    private IReadOnlyDictionary<int, ushort>? ReadModeWords(uint pri)
    {
        if (!bomb) return null;

        var words = new Dictionary<int, ushort>();

        foreach (var (target, path) in new (int, string)[]
        {
            (26, "UTGame.TGRoundPlayerReplicationInfo.WinDuelKill"),
            (58, "UTGame.TGRoundPlayerReplicationInfo.TimesElite"),
            (60, "UTGame.TGBMPlayerReplicationInfo.TimesKillC4Carrier"),
            (62, "UTGame.TGBMPlayerReplicationInfo.TimesKillWithC4"),
            (64, "UTGame.TGBMPlayerReplicationInfo.TeamSaver")
        })
            words.Add(target, unchecked((ushort)reader.U32(Field(pri, path, "IntProperty", 4))));

        return words;
    }

    private byte[] ReadRounds(uint gri)
    {
        uint address = Field(gri, "UTGame.TGRoundGameReplicationInfo.RoundInfoList", "ArrayProperty", 12);
        uint field = reader.FindField(reader.U32(gri + 0x34), "RoundInfoList", "UTGame.TGRoundGameReplicationInfo.RoundInfoList");
        uint inner = reader.U32(field + 0x84), structure = reader.U32(inner + 0x84);

        if (reader.Path(reader.U32(inner + 0x34)) != "Core.StructProperty" ||
            reader.Path(structure) != "UTGame.TGRoundGameReplicationInfo.RoundInfo" || reader.U32(inner + 0x44) != 56)
            throw new InvalidDataException("爆破小局结构布局变化。");

        foreach (var (name, offset) in new (string, uint)[]
        {
            ("CommunicateTime", 4), ("PlantC4Uin", 24), ("DefuseC4Uin", 36),
            ("bC4Explode", 48), ("WinningTeamIndex", 52)
        })
        {
            uint property = reader.FindField(structure, name, "UTGame.TGRoundGameReplicationInfo.RoundInfo." + name);

            if (reader.U32(property + 0x60) != offset)
                throw new InvalidDataException("爆破小局字段偏移变化：" + name);
        }

        var rounds = ReadArray(address, 56, 32);
        var writer = new TdrWriter();

        for (int offset = 0; offset < rounds.Bytes.Length; offset += 56)
        {
            var row = rounds.Bytes.AsSpan(offset, 56);
            uint camp = BinaryPrimitives.ReadUInt32LittleEndian(row[52..]);

            if (camp > 2) throw new InvalidDataException("爆破小局胜方无效。");

            string planter = ReadString(row.Slice(24, 12)), defuser = ReadString(row.Slice(36, 12));

            writer.U8((byte)camp).U8(unchecked((byte)BinaryPrimitives.ReadUInt32LittleEndian(row[4..])))
                .U8(0).U16(0).U64(ParseUin(planter)).U64(ParseUin(defuser))
                .U8((BinaryPrimitives.ReadUInt32LittleEndian(row[48..]) & 1) != 0 ? (byte)1 : (byte)0);
        }
        CheckArray(rounds);

        return writer.ToArray();

        static ulong ParseUin(string text) => string.IsNullOrEmpty(text) ? 0 :
            ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ? uin :
            throw new InvalidDataException("爆破埋雷或拆雷原生 UIN 无效。");
    }

    private bool IsCompleted(uint game, uint gri)
    {
        bool gameEnded = Bit(game, "Engine.GameInfo.bGameEnded");
        bool matchOver = Bit(gri, "Engine.GameReplicationInfo.bMatchIsOver");

        return gameEnded && matchOver;
    }

    private byte ReadCamp(uint team)
    {
        if (team == 0) return 0;

        reader.Identity(team);

        if (!reader.IsA(team, "Engine.TeamInfo"))
            throw new InvalidDataException("结算队伍对象类型无效。");

        uint camp = reader.U32(Field(team, "Engine.TeamInfo.TeamIndex", "IntProperty", 4, 0x1E8));

        if (camp > 1) throw new InvalidDataException("团队战结算阵营超出原生范围。");

        return (byte)camp;
    }

    private byte ReadWinningCamp(uint team) => team == 0 ? (byte)2 : ReadCamp(team);

    private ushort[] ReadTeamScores(uint gri)
    {
        var teams = ReadArray(Field(gri, "Engine.GameReplicationInfo.Teams", "ArrayProperty", 12), 4, 2);

        if (teams.Bytes.Length != 8) throw new InvalidDataException("团队战必须有两支原生队伍。");

        var scores = new ushort[2];

        for (int index = 0; index < 2; index++)
        {
            uint team = U32(teams.Bytes, index * 4);
            byte camp = ReadCamp(team);
            float score = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(
                Field(team, "Engine.TeamInfo.Score", "FloatProperty", 4, 0x1E4), 4));

            if (!float.IsFinite(score) || score < int.MinValue || score >= int.MaxValue)
                throw new InvalidDataException("原生团队分数无效。");

            scores[camp] = unchecked((ushort)(int)score);
        }
        CheckArray(teams);

        return scores;
    }

    private ulong ReadAce(uint gri)
    {
        uint pri = reader.U32(Field(gri, "UTGame.TGGameReplicationInfo.LeadPRI", "ObjectProperty", 4, 0x28C));

        if (pri == 0 || !reader.IsA(pri, "UTGame.TG" + prefix + "PlayerReplicationInfo")) return 0;

        string text = ReadUin(pri, process.ReadMemory(pri, 0xB70));

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ? uin : 0;
    }

    private sealed record PlayerSample(MatchStartMember Member, PvpStatistics Statistics, bool Completed);
}
