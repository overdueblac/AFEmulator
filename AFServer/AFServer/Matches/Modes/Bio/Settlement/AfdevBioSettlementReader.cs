using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reads ordinary-BIO OnlineDS reports using same-version AF2014/AFDEV layout at 0x0146A7D0. Validates code
/// fingerprints and live world first, without replacing bot-isolation hooks or calling native reporting
/// that increments statistics.
/// </summary>
internal sealed class AfdevBioSettlementReader : AfdevSettlementReader
{
    private readonly Dictionary<Guid, PlayerSample> players = [];
    private readonly Dictionary<Guid, MatchSettlementSpectator> spectators = [];

    internal AfdevBioSettlementReader(AfdevOwnedProcess process, MatchStartSnapshot start, AfdevNativeEvidence world)
        : base(process, start, world)
    {
        if (!AfdevMapCatalog.MatchesBio(world))
            throw new InvalidDataException("BIO 结算世界不属于已接入的普通变异战。");

        // Both ranges avoid the exact bot-isolation entry at 0x0146AAF1, so validation never requires removing existing hooks.
        CheckCode(0x0146A95C, 0x57, "B04CC44A1E1A6BF9FE506F0C14E4D79D6462A2BE962CE735484FC59330567584");
        CheckCode(0x0146AB46, 0xDD8, "F0EAF5CCAD3B42AF1CF5AB4FFF33EC78BA1277275237E2D540A48767CA55BBD0");
    }

    internal override MatchSettlementSnapshot Capture(AfdevNativeEvidence world)
    {
        CheckWorld(world);
        if (completed is not null) return completed;

        uint gri = reader.U32(Field(world.GameInfo, "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4, 0x2A8));
        var griIdentity = reader.Identity(gri);

        if (!reader.IsA(gri, "TGBioGame.TGBioGameReplicationInfo"))
            throw new InvalidDataException("结算的 GRI 不是本局普通 BIO 对象。");

        bool ended = IsCompleted(world.GameInfo, gri);
        float worldTime = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(
            Field(world.WorldInfo, "Engine.WorldInfo.TimeSeconds", "FloatProperty", 4, 0x394), 4));
        byte[] game = process.ReadMemory(world.GameInfo, 0x650);
        byte[] gameStats = process.ReadMemory(gri, 0x3D0);
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

            if (!reader.IsA(pri, "TGBioGame.TGBioPlayerReplicationInfo")) continue;

            byte[] values = process.ReadMemory(pri, 0x10D8);
            string uinText = ReadUin(pri, values);

            // Bot bot-<id> identities cannot become accounts. Native objects outside the frozen roster cannot enter settlement.
            if (!ulong.TryParse(uinText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ||
                !members.TryGetValue(uin, out var member) || Bit(pri, "Engine.PlayerReplicationInfo.bBot") ||
                (U32(values, 0x3C0) & 0x30000) != 0)
                continue;

            if (retiredSessions.Contains(member.Session.SessionId)) continue;

            if (reader.U32(Field(pri, "Engine.Actor.WorldInfo", "ObjectProperty", 4, 0xEC)) != world.WorldInfo)
                throw new InvalidDataException("参战 PRI 已不属于当前世界。");

            uint controller = reader.U32(Field(pri, "Engine.Actor.Owner", "ObjectProperty", 4));

            // Removed PRIs retain only previously validated samples. Actual connection ownership is required for initial registration or reward updates.
            if (controller < 0x10000 || !reader.IsA(controller, "TGBioGame.TGBioPlayerController")) continue;

            var controllerIdentity = reader.Identity(controller);
            uint connection = reader.U32(Field(controller, "Engine.PlayerController.Player", "ObjectProperty", 4));

            if (connection < 0x10000 || reader.U32(connection + 0x60) != world.NetDriver ||
                reader.U32(connection + 0x40) != controller ||
                reader.U32(Field(controller, "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4)) != pri)
                continue;

            byte camp = member.Observer ? values[0xB24] : ReadCamp(U32(values, 0x204));

            if (member.Observer)
            {
                freshSpectators.Add(member.Session.SessionId, new MatchSettlementSpectator(member, camp));
            }
            else
            {
                var zombies = ReadArray(pri + 0x1048, 28, 256, 12);
                var weapons = ReadArray(pri + 0xF10, 144, 4096, 16);
                var statistics = BioSettlementStatistics.ReadPlayer(uin, camp, worldTime, values, zombies.Bytes, weapons.Bytes);

                freshPlayers.Add(member.Session.SessionId, new PlayerSample(member, statistics, ended));
                CheckArray(zombies);
                CheckArray(weapons);
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
        var writer = BioSettlementStatistics.WriteGame(game, gameStats, ordered.Length,
            ReadWinningCamp(U32(gameStats, 0x20C)));

        foreach (var sample in ordered) sample.Statistics.WriteTo(writer);

        var result = new MatchSettlementSnapshot(start, world.AttemptId, world.WorldGeneration, ended, observed,
            [.. ordered.Select(sample => new MatchSettlementPlayer(sample.Member, sample.Statistics.Camp,
                sample.Completed ? (uint)sample.Statistics.GameCoin : 0))], orderedSpectators, [.. writer.ToArray()]);

        // Final match-end samples stop increasing with departure/observation time. Individual round end does not satisfy both native flags.
        if (ended) completed = result;

        return result;
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

        if (camp > 1) throw new InvalidDataException("BIO 结算阵营超出原生范围。");

        return (byte)camp;
    }

    private byte ReadWinningCamp(uint team) => team == 0 ? (byte)2 : (byte)(1 - ReadCamp(team));

    private sealed record PlayerSample(MatchStartMember Member, BioSettlementStatistics Statistics, bool Completed);
}
