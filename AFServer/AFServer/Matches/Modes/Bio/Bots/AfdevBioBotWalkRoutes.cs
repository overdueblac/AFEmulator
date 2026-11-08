using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Player walking-demonstration library for the current map. Stores anonymous geometry/body dimensions,
/// never native addresses. Every graph edge is a candidate; bots still check each segment natively. After
/// precheck rejection, only nearby adjacent segments permit bounded trial walking. Demonstrations do not
/// prove reachability. File errors disable only that demonstration, not the native match.
/// </summary>
internal sealed partial class AfdevBioBotWalkRoutes
{
    private const int MaximumPaths = 128;
    private const int MaximumPoints = 4096;
    private const int MaximumPathPoints = 256;
    private static readonly SemaphoreSlim persistenceGate = new(1, 1);
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly uint modeId;
    private readonly ushort mapId;
    private readonly string mapHash;
    private readonly string learnedPath;
    private readonly Action<string> log;
    private readonly AfdevBioBotWalkPath[] seed;
    private AfdevBioBotWalkGraph graph;
    private int revision;

    private AfdevBioBotWalkRoutes(uint modeId, ushort mapId, string mapHash, string learnedPath, AfdevBioBotWalkPath[] seed,
        AfdevBioBotWalkPath[] learned, Action<string> log)
    {
        this.modeId = modeId;
        this.mapId = mapId;
        this.mapHash = mapHash;
        this.learnedPath = learnedPath;
        this.seed = seed;
        this.log = log;
        graph = AfdevBioBotWalkGraph.Build(Bound(seed.Concat(learned)), ++revision);
    }

    public AfdevBioBotWalkGraph Snapshot => Volatile.Read(ref graph);

    public static async Task<AfdevBioBotWalkRoutes> LoadAsync(AfdevInstanceWorkspace workspace, AfdevNativeEvidence world,
        Action<string> log, CancellationToken token)
    {
        AfdevBioBotMapIdentity identity = await AfdevBioBotMapIdentity.ReadAsync(workspace, world, token).ConfigureAwait(false);
        string hash = identity.MainMapSha256;
        string seeded = Path.Combine(AppContext.BaseDirectory, "data", "bio", identity.SeedFileName);
        string learned = Path.Combine(AppContext.BaseDirectory, "runtime", "bot-ai", hash + "-walk-routes.json");

        AfdevBioBotWalkPath[] Read(string path)
        {
            try
            {
                return ReadArchive(path, hash).Paths;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                log($"BIO_WALK_ROUTES_IGNORED file={Path.GetFileName(path)} reason={error.Message}");
                return [];
            }
        }

        var result = new AfdevBioBotWalkRoutes(identity.ModeId, identity.MapId, hash, learned, Read(seeded), Read(learned), log);

        log($"BIO_WALK_ROUTES_LOADED points={result.Snapshot.Points.Count} map={world.MapName} sha256={hash}; 示范仅提供候选，每段由原生执行并核对到达。");
        return result;
    }

    /// <summary>
    /// Absorbs continuous walking after recording completes, serializing merges with other rooms and saving
    /// atomically for later matches.
    /// </summary>
    public async Task ImportRecordingAsync(string recording, CancellationToken token)
    {
        AfdevBioBotWalkArchive imported = ExtractRecording(recording, mapHash, modeId, mapId);

        if (imported.Paths.Length == 0)
            return;

        await persistenceGate.WaitAsync(token).ConfigureAwait(false);

        string? temporary = null;

        try
        {
            AfdevBioBotWalkArchive previous = ReadArchive(learnedPath, mapHash);
            AfdevBioBotWalkPath[] paths = Bound(previous.Paths.Concat(imported.Paths));
            var archive = new AfdevBioBotWalkArchive(1, mapHash, paths);

            Directory.CreateDirectory(Path.GetDirectoryName(learnedPath)!);
            temporary = learnedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(archive, jsonOptions), token).ConfigureAwait(false);
            File.Move(temporary, learnedPath, overwrite: true);
            temporary = null;
            Volatile.Write(ref graph, AfdevBioBotWalkGraph.Build(Bound(seed.Concat(paths)), ++revision));
            log($"BIO_WALK_ROUTES_LEARNED imported={imported.Paths.Length} stored={paths.Length} points={Snapshot.Points.Count}; 新示范下次执行仍需原生检查。");
        }
        finally
        {
            try
            {
                if (temporary != null && File.Exists(temporary))
                    File.Delete(temporary);
            }
            finally
            {
                persistenceGate.Release();
            }
        }
    }

    private static AfdevBioBotWalkArchive ReadArchive(string path, string hash)
    {
        if (!File.Exists(path))
            return new AfdevBioBotWalkArchive(1, hash, []);
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("行走示范库超过四 MiB 上限。");

        using var stream = File.OpenRead(path);
        AfdevBioBotWalkArchive? archive = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Deserialize<AfdevBioBotWalkArchive>(stream, jsonOptions);

        if (archive == null || archive.FormatVersion != 1 || archive.MainMapSha256 != hash || archive.Paths == null ||
            archive.Paths.Length > MaximumPaths || archive.Paths.Any(item => !ValidPath(item)) ||
            archive.Paths.Sum(item => item.Points.Length) > MaximumPoints)
            throw new InvalidDataException("行走示范库版本、地图哈希、体型或几何边界不同。");

        return archive;
    }

    private static bool ValidPath(AfdevBioBotWalkPath path) => path != null &&
        path.Id is { Length: 64 } && path.Id.All(Uri.IsHexDigit) &&
        path.Radius is >= 18 and <= 40 && path.Height is >= 35 and <= 48 &&
        path.Points is { Length: >= 2 and <= MaximumPathPoints } && path.Points.All(point => point.IsValid) &&
        path.Points.Zip(path.Points.Skip(1)).All(pair => DistanceSquared(pair.First, pair.Second) <= 256 * 256 &&
            Math.Abs(pair.First.Z - pair.Second.Z) <= 100);

    private static AfdevBioBotWalkPath[] Bound(IEnumerable<AfdevBioBotWalkPath> source)
    {
        var paths = new List<AfdevBioBotWalkPath>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;

        foreach (AfdevBioBotWalkPath path in source.Reverse())
        {
            if (!ValidPath(path) || !ids.Add(path.Id) || paths.Count >= MaximumPaths || count + path.Points.Length > MaximumPoints)
                continue;

            paths.Add(path);
            count += path.Points.Length;
        }

        paths.Reverse();
        return paths.ToArray();
    }

    internal static double DistanceSquared(AfdevBioBotPoint left, AfdevBioBotPoint right) =>
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2) + Math.Pow(left.Z - right.Z, 2);
}

internal sealed record AfdevBioBotWalkArchive(int FormatVersion, string MainMapSha256, AfdevBioBotWalkPath[] Paths);
internal sealed record AfdevBioBotWalkPath(string Id, float Radius, float Height, AfdevBioBotPoint[] Points);
