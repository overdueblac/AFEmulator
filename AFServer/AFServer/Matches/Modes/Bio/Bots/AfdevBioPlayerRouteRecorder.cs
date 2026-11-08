using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Read-only human-route sampler for this round. Uses only frozen roster/current native connections,
/// splitting on disconnect, Pawn/round changes, or abnormal movement. Output is an unverified
/// demonstration, not a ready-to-use bot edge. File failure disables recording without affecting gameplay.
/// </summary>
internal sealed partial class AfdevBioPlayerRouteRecorder(AfdevOwnedProcess process, AfdevNativeEvidence world,
    AfdevInstanceWorkspace workspace, IReadOnlyList<AfdevBioBotHumanIdentity> humans, Action<string> log, Action checkBinding)
{
    private const long MaximumBytes = 16 * 1024 * 1024;
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly Dictionary<(uint Class, string Path), uint> fields = [];
    private readonly Dictionary<(uint Class, string Path), uint> masks = [];
    private readonly Dictionary<string, int> humanSlots = humans.Where(human => !human.Observer)
        .Select((human, index) => (Uin: human.Uin.ToString(CultureInfo.InvariantCulture), Slot: index + 1))
        .ToDictionary(item => item.Uin, item => item.Slot, StringComparer.Ordinal);
    private readonly Dictionary<int, Track> tracks = [];

    public async Task RunAsync(CancellationToken token)
    {
        string directory = Path.Combine(workspace.Directory, "logs", "bio-bot");
        string path = Path.Combine(directory, "player-routes.jsonl");
        long samples = 0;
        long bytes = 0;
        long skipped = 0;
        string reason = "cancelled";
        var elapsed = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(directory);

            await using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 16384, FileOptions.Asynchronous), new UTF8Encoding(false));
            AfdevBioBotMapIdentity identity = await AfdevBioBotMapIdentity.ReadAsync(workspace, world, token).ConfigureAwait(false);
            string mapHash = identity.MainMapSha256;

            async Task<bool> WriteAsync(object value)
            {
                string json = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(value);
                int length = Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);

                if (bytes + length > MaximumBytes)
                    return false;

                await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
                bytes += length;
                return true;
            }

            await WriteAsync(new RunAsyncJson1
            {
                Kind = "metadata", FormatVersion = 1, InstanceId = world.InstanceId, AttemptId = world.AttemptId, AllocationId = world.AllocationId,
                WorldGeneration = world.WorldGeneration, ModeId = world.ModeId, MapId = world.MapId, MapName = world.MapName, AfdevSha256 = world.AfdevSha256,
                MainMapSha256 = mapHash, StartedAtUtc = DateTimeOffset.UtcNow, SampleIntervalMs = 200,
                MaximumBytes = MaximumBytes, MaximumMinutes = 30, HumanSlots = humanSlots.Count, VerifiedForBotReplay = false
            }).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
            log($"BIO_PLAYER_ROUTE_RECORDING path={path}; 仅记录本局真人示范，尚未学习或回放。");

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));

            while (elapsed.Elapsed < TimeSpan.FromMinutes(30) && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!process.IsAlive)
                {
                    reason = "process-stopped";
                    break;
                }

                IReadOnlyList<Observation> observations;

                try
                {
                    checkBinding();
                    observations = ReadObservations();
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
                {
                    // Native connections/Pawns may change between reads. Time gaps split missing samples; never join stale trajectories.
                    skipped++;
                    continue;
                }

                foreach (Observation observation in observations)
                {
                    long now = elapsed.ElapsedMilliseconds;
                    Track? previous = tracks.GetValueOrDefault(observation.HumanSlot);
                    bool split = previous == null || previous.Pawn != observation.Pawn || previous.Controller != observation.Controller ||
                        previous.Round != observation.Round || previous.Team != observation.Team ||
                        previous.Physics != observation.Physics || previous.Radius != observation.Radius || previous.Height != observation.Height ||
                        now - previous.TimeMs > 700 ||
                        DistanceSquared(previous.Location, observation.Location) > 768 * 768;
                    long segment = previous == null ? 1 : previous.Segment + (split ? 1 : 0);

                    tracks[observation.HumanSlot] = new Track(observation.Pawn, observation.Controller, observation.Round,
                        now, segment, observation.Location, observation.Team, observation.Physics, observation.Radius, observation.Height);

                    if (!await WriteAsync(new RunAsyncJson2
                    {
                        Kind = "sample", Sequence = samples + 1, TimeMs = now, ObservedAtUtc = DateTimeOffset.UtcNow,
                        HumanSlot = observation.HumanSlot, Segment = segment, SegmentStart = split, Round = observation.Round, Team = observation.Team,
                        PawnClass = observation.PawnClass, Location = observation.Location, Velocity = observation.Velocity, Physics = observation.Physics,
                        Radius = observation.Radius, Height = observation.Height
                    }).ConfigureAwait(false))
                    {
                        reason = "size-limit";
                        return;
                    }

                    samples++;
                }

                // Sampling runs in a managed background task, without per-frame file writes on the game thread.
                await writer.FlushAsync(token).ConfigureAwait(false);
            }

            if (elapsed.Elapsed >= TimeSpan.FromMinutes(30))
                reason = "duration-limit";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            reason = "recording-error";
            log($"BIO_PLAYER_ROUTE_RECORDING_DISABLED kind={error.GetType().Name} reason={error.Message}");
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "player-routes-summary.json"), global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(new RunAsyncJson3
                {
                    Samples = samples, Bytes = bytes, SkippedSnapshots = skipped, Reason = reason,
                    DurationMs = elapsed.ElapsedMilliseconds, StoppedAtUtc = DateTimeOffset.UtcNow,
                    Segments = tracks.ToDictionary(item => item.Key, item => item.Value.Segment), VerifiedForBotReplay = false
                }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($"BIO_PLAYER_ROUTE_SUMMARY_FAILED kind={error.GetType().Name}");
            }

            log($"BIO_PLAYER_ROUTE_RECORDING_STOPPED samples={samples} bytes={bytes} reason={reason}");
        }
    }

    private IReadOnlyList<Observation> ReadObservations()
    {
        if (humanSlots.Count == 0 || !Bit(world.GameInfo, "UTGame.TGRoundMatch.bCanMove") ||
            !Bit(world.GameInfo, "UTGame.TGRoundMatch.bInRound"))
            return [];
        if (reader.U32(world.World + 0xD8) != world.NetDriver)
            throw new InvalidDataException("路线采样 NetDriver 已换代。");

        byte[] header = process.ReadMemory(world.NetDriver + 0x44, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));

        if (count > capacity || capacity > 64 || count != 0 && data < 0x10000)
            throw new InvalidDataException("路线采样连接数组无效。");
        if (count == 0)
            return [];

        byte[] connections = process.ReadMemory(data, checked((int)count * 4));
        int round = unchecked((int)reader.U32(Field(world.GameInfo, "UTGame.TGRoundMatch.RoundCount", "IntProperty")));
        var result = new List<Observation>();

        for (int offset = 0; offset < connections.Length; offset += 4)
        {
            uint connection = BinaryPrimitives.ReadUInt32LittleEndian(connections.AsSpan(offset));

            if (connection < 0x10000 || reader.U32(connection + 0x60) != world.NetDriver)
                continue;

            Observation? observation = ReadHuman(connection, round);

            if (observation != null)
                result.Add(observation);
        }

        return reader.U32(world.World + 0xD8) == world.NetDriver &&
            process.ReadMemory(world.NetDriver + 0x44, 12).AsSpan().SequenceEqual(header) &&
            process.ReadMemory(data, connections.Length).AsSpan().SequenceEqual(connections) ? result : [];
    }

    private Observation? ReadHuman(uint connection, int round)
    {
        uint controller = reader.U32(connection + 0x40);

        if (controller < 0x10000 || !reader.IsA(controller, "TGBioGame.TGBioPlayerController"))
            return null;

        QueryObject controllerIdentity = reader.Identity(controller);
        uint pri = reader.U32(Field(controller, "Engine.Controller.PlayerReplicationInfo"));
        uint pawn = reader.U32(Field(controller, "Engine.Controller.Pawn"));

        if (pri < 0x10000 || pawn < 0x10000 || !reader.IsA(pri, "TGBioGame.TGBioPlayerReplicationInfo") ||
            !reader.IsA(pawn, "TGBioGame.TGBioPawn") || Deleted(controller) || Deleted(pri) || Deleted(pawn))
            return null;

        QueryObject priIdentity = reader.Identity(pri);
        QueryObject pawnIdentity = reader.Identity(pawn);
        string? uin = ReadUin(pri);

        if (uin == null || !humanSlots.TryGetValue(uin, out int slot) || Bit(pri, "Engine.PlayerReplicationInfo.bBot") ||
            Bit(pri, "Engine.PlayerReplicationInfo.bOnlySpectator") || Bit(pri, "Engine.PlayerReplicationInfo.bIsSpectator") ||
            Bit(pri, "UTGame.TGPlayerReplicationInfo.bAlwaysObserver") ||
            reader.U32(Field(controller, "Engine.PlayerController.Player")) != connection ||
            reader.U32(Field(pri, "Engine.Actor.Owner")) != controller ||
            reader.U32(Field(pawn, "Engine.Pawn.Controller")) != controller ||
            reader.U32(Field(pawn, "Engine.Pawn.PlayerReplicationInfo")) != pri ||
            unchecked((int)reader.U32(Field(pawn, "Engine.Pawn.Health", "IntProperty"))) <= 0)
            return null;

        foreach (uint actor in new[] { controller, pri, pawn })
            if (reader.U32(Field(actor, "Engine.Actor.WorldInfo")) != world.WorldInfo)
                return null;

        uint team = reader.U32(Field(pri, "Engine.PlayerReplicationInfo.Team"));
        uint cylinder = reader.U32(Field(pawn, "Engine.Pawn.CylinderComponent", "ComponentProperty"));

        if (team < 0x10000 || cylinder < 0x10000)
            return null;

        int teamIndex = unchecked((int)reader.U32(Field(team, "Engine.TeamInfo.TeamIndex", "IntProperty")));
        float[] location = Vector(pawn, "Engine.Actor.Location");
        float[] velocity = Vector(pawn, "Engine.Actor.Velocity");
        float radius = Float(cylinder, "Engine.CylinderComponent.CollisionRadius");
        float height = Float(cylinder, "Engine.CylinderComponent.CollisionHeight");
        byte physics = process.ReadMemory(Field(pawn, "Engine.Actor.Physics", "ByteProperty", 1), 1)[0];

        if (teamIndex is not (0 or 1) || radius <= 0 || height <= 0 || !float.IsFinite(radius) || !float.IsFinite(height) ||
            reader.U32(connection + 0x40) != controller || reader.U32(connection + 0x60) != world.NetDriver ||
            reader.U32(Field(controller, "Engine.Controller.Pawn")) != pawn ||
            reader.U32(Field(controller, "Engine.Controller.PlayerReplicationInfo")) != pri ||
            reader.Identity(controller) != controllerIdentity || reader.Identity(pri) != priIdentity || reader.Identity(pawn) != pawnIdentity)
            return null;

        return new Observation(slot, controllerIdentity, pawnIdentity, reader.Path(pawnIdentity.Class), round,
            teamIndex, location, velocity, physics, radius, height);
    }

    private string? ReadUin(uint pri)
    {
        uint field = Field(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12);
        byte[] header = process.ReadMemory(field, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));

        if (count is < 2 or > 33 || count > capacity || capacity > 65535 || data < 0x10000)
            return null;

        byte[] value = process.ReadMemory(data, checked((int)count * 2));

        return value[^1] == 0 && value[^2] == 0 && process.ReadMemory(field, 12).AsSpan().SequenceEqual(header)
            ? Encoding.Unicode.GetString(value, 0, value.Length - 2) : null;
    }

    private float[] Vector(uint receiver, string path)
    {
        byte[] bytes = process.ReadMemory(Field(receiver, path, "StructProperty", 12), 12);
        float[] value = [BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8))];

        if (value.Any(component => !float.IsFinite(component)))
            throw new InvalidDataException("路线采样包含非有限坐标或速度。");

        return value;
    }

    private float Float(uint receiver, string path) => BitConverter.Int32BitsToSingle(unchecked((int)reader.U32(Field(receiver, path, "FloatProperty"))));

    private bool Bit(uint receiver, string path)
    {
        uint type = reader.U32(receiver + 0x34);
        uint field = Field(receiver, path, "BoolProperty");

        if (!masks.TryGetValue((type, path), out uint mask))
        {
            uint property = reader.FindField(type, path[(path.LastIndexOf('.') + 1)..], path);

            mask = reader.U32(property + 0x84);

            if (mask == 0 || (mask & (mask - 1)) != 0)
                throw new InvalidDataException("路线采样布尔属性不是单一位。");

            masks.Add((type, path), mask);
        }

        return (reader.U32(field) & mask) != 0;
    }

    private uint Field(uint receiver, string path, string kind = "ObjectProperty", uint size = 4)
    {
        uint type = reader.U32(receiver + 0x34);

        if (!fields.TryGetValue((type, path), out uint offset))
        {
            offset = reader.FieldAddress(receiver, path[(path.LastIndexOf('.') + 1)..], path, kind, size) - receiver;
            fields.Add((type, path), offset);
        }

        return receiver + offset;
    }

    private bool Deleted(uint actor) => (reader.U32(actor + 0x0C) & 0x20000000) != 0 || (reader.U32(actor + 0xB0) & 8) != 0;
    private static double DistanceSquared(float[] left, float[] right) => left.Select((value, index) => Math.Pow(value - right[index], 2)).Sum();
    private sealed record Observation(int HumanSlot, QueryObject Controller, QueryObject Pawn, string PawnClass,
        int Round, int Team, float[] Location, float[] Velocity, byte Physics, float Radius, float Height);
    private sealed record Track(QueryObject Pawn, QueryObject Controller, int Round, long TimeMs, long Segment, float[] Location,
        int Team, byte Physics, float Radius, float Height);
}
