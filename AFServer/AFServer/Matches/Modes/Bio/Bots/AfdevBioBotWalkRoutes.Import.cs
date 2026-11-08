using System.Security.Cryptography;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Bots;

internal sealed partial class AfdevBioBotWalkRoutes
{
    /// <summary>
    /// Accepts recorded walking on this local map only. Falls, jumps, crouching, body-size changes, and
    /// time gaps break paths.
    /// </summary>
    internal static AfdevBioBotWalkArchive ExtractRecording(string path, string expectedMapHash, uint modeId, ushort mapId)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("玩家示范超过录制器的十六 MiB 边界。");

        using var reader = File.OpenText(path);
        using JsonDocument metadata = JsonDocument.Parse(reader.ReadLine() ?? throw new InvalidDataException("玩家示范缺少元数据。"));
        JsonElement header = metadata.RootElement;

        if (!AfdevMapCatalog.SupportsBioBots(modeId, mapId) ||
            header.GetProperty("Kind").GetString() != "metadata" || header.GetProperty("FormatVersion").GetInt32() != 1 ||
            header.GetProperty("MainMapSha256").GetString() != expectedMapHash ||
            header.GetProperty("ModeId").GetUInt32() != modeId || header.GetProperty("MapId").GetUInt32() != mapId)
            throw new InvalidDataException("玩家示范不属于当前普通变异战地图版本。");

        var pending = new Dictionary<int, WalkTrack>();
        var paths = new List<AfdevBioBotWalkPath>();

        void Flush(WalkTrack track)
        {
            AfdevBioBotPoint[] points = Simplify(track.Points);

            for (int offset = 0; offset < points.Length - 1; offset += MaximumPathPoints - 1)
            {
                AfdevBioBotPoint[] chunk = points.Skip(offset).Take(MaximumPathPoints).ToArray();

                if (chunk.Length < 2 || chunk.Zip(chunk.Skip(1)).Sum(pair => Math.Sqrt(DistanceSquared(pair.First, pair.Second))) < 96)
                    continue;

                byte[] geometry = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToUtf8Bytes(new ExtractRecordingJson1 { Radius = track.Last.Radius, Height = track.Last.Height, Points = chunk });
                string id = Convert.ToHexStringLower(SHA256.HashData(geometry));
                var route = new AfdevBioBotWalkPath(id, track.Last.Radius, track.Last.Height, chunk);

                if (ValidPath(route))
                    paths.Add(route);
            }
        }

        while (reader.ReadLine() is { } line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement item = document.RootElement;

            if (item.GetProperty("Kind").GetString() != "sample")
                continue;

            int slot = item.GetProperty("HumanSlot").GetInt32();
            JsonElement location = item.GetProperty("Location");
            var sample = new WalkSample(item.GetProperty("Segment").GetInt64(), item.GetProperty("TimeMs").GetInt64(),
                item.GetProperty("Round").GetInt32(), item.GetProperty("Team").GetInt32(),
                item.GetProperty("PawnClass").GetString() ?? string.Empty, item.GetProperty("Radius").GetSingle(),
                item.GetProperty("Height").GetSingle(), item.GetProperty("Physics").GetInt32(),
                new AfdevBioBotPoint(location[0].GetSingle(), location[1].GetSingle(), location[2].GetSingle()));
            WalkTrack? previous = pending.GetValueOrDefault(slot);
            bool walk = slot is >= 1 and <= 64 && sample.Physics == 1 && sample.Team is 0 or 1 && sample.Point.IsValid &&
                sample.Radius is >= 18 and <= 40 && sample.Height is >= 35 and <= 48;
            bool continuous = previous != null && walk && previous.Last.Segment == sample.Segment &&
                previous.Last.Round == sample.Round && previous.Last.Team == sample.Team && previous.Last.PawnClass == sample.PawnClass &&
                previous.Last.Radius == sample.Radius && previous.Last.Height == sample.Height &&
                sample.TimeMs - previous.Last.TimeMs is > 0 and <= 800 &&
                DistanceSquared(previous.Last.Point, sample.Point) <= 200 * 200 && Math.Abs(previous.Last.Point.Z - sample.Point.Z) <= 80;

            if (previous != null && !continuous)
            {
                Flush(previous);
                pending.Remove(slot);
                previous = null;
            }

            if (!walk)
                continue;
            if (previous == null)
            {
                previous = new WalkTrack(sample);
                pending[slot] = previous;
            }

            if (previous.Points.Count == 0 || DistanceSquared(previous.Points[^1], sample.Point) >= 16 * 16)
                previous.Points.Add(sample.Point);

            previous.Last = sample;
        }

        foreach (WalkTrack track in pending.Values)
            Flush(track);

        return new AfdevBioBotWalkArchive(1, expectedMapHash, Bound(paths));
    }

    private static AfdevBioBotPoint[] Simplify(IReadOnlyList<AfdevBioBotPoint> source)
    {
        if (source.Count < 3)
            return source.ToArray();

        var result = new List<AfdevBioBotPoint> { source[0] };

        for (int index = 1; index < source.Count - 1; index++)
        {
            AfdevBioBotPoint previous = result[^1];
            AfdevBioBotPoint current = source[index];
            AfdevBioBotPoint next = source[index + 1];
            double ax = current.X - previous.X, ay = current.Y - previous.Y;
            double bx = next.X - current.X, by = next.Y - current.Y;
            double lengths = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
            bool turning = lengths > 1 && (ax * bx + ay * by) / lengths < 0.92;

            if (DistanceSquared(previous, current) >= 112 * 112 || Math.Abs(previous.Z - current.Z) >= 40 ||
                turning && DistanceSquared(previous, current) >= 32 * 32 || DistanceSquared(previous, next) >= 200 * 200 ||
                Math.Abs(previous.Z - next.Z) > 80)
                result.Add(current);
        }

        if (source[^1] != result[^1])
            result.Add(source[^1]);

        return result.ToArray();
    }

    private sealed record WalkSample(long Segment, long TimeMs, int Round, int Team, string PawnClass,
        float Radius, float Height, int Physics, AfdevBioBotPoint Point);

    private sealed class WalkTrack(WalkSample last)
    {
        public WalkSample Last { get; set; } = last;
        public List<AfdevBioBotPoint> Points { get; } = [];
    }
}
