using System.Security.Cryptography;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Exact map and resource version for route demonstrations, derived from this match's native evidence and
/// protected main-map file.
/// </summary>
internal sealed record AfdevBioBotMapIdentity(uint ModeId, ushort MapId, string MainMapSha256)
{
    /// <summary>
    /// Map-demonstration name shipped with the program. A missing file means no bundled demonstration
    /// exists for this map.
    /// </summary>
    internal string SeedFileName => MapId switch
    {
        0x004A => "factory-22-walk-routes.json",
        0x007A => "maya-7-walk-routes.json",
        0x0027 => "capital-4-walk-routes.json",
        0x0076 => "capital-12-walk-routes.json",
        0x003C => "maya-6-walk-routes.json",
        _ => throw new InvalidDataException("路线示范不能使用未接入地图。")
    };

    /// <summary>
    /// Locates the actual main map through this resource manifest and verifies its hash, independent of
    /// warmup defaults or fixed subdirectories.
    /// </summary>
    internal static async Task<AfdevBioBotMapIdentity> ReadAsync(AfdevInstanceWorkspace workspace,
        AfdevNativeEvidence world, CancellationToken token)
    {
        if (world.InstanceId != workspace.InstanceId || !AfdevMapCatalog.MatchesBio(world))
            throw new InvalidDataException("路线示范的地图证据不属于当前工作区或已接入路由。");

        RuntimeManifest manifest = RuntimeManifest.Load(Path.Combine(workspace.Directory, "runtime-manifest.json"));

        if (world.ManifestVersion != manifest.RuntimeVersion ||
            !world.AfdevSha256.Equals(manifest.AfdevSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路线示范的世界与资源清单版本不一致。");

        RuntimeManifestFile mainMap = manifest.ResolveMainMap(world.MapName);
        await using var map = File.OpenRead(RuntimePath.ResolveUnderRoot(workspace.RuntimeRoot, mainMap.RelativePath));
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(map, token).ConfigureAwait(false));

        if (map.Length != mainMap.Size || !hash.Equals(mainMap.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路线示范的主地图文件与本局资源清单不一致。");

        return new AfdevBioBotMapIdentity(world.ModeId, world.MapId, hash);
    }
}
