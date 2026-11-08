using System.Security.Cryptography;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Validates AFDEV deployment integrity without connecting to MongoDB, modifying the client, or claiming
/// the native match is ready.
/// </summary>
public static class AfdevRuntimePreflight
{
    /// <summary>
    /// Checks manifest files, native patches, and exact map routes. The caller must exclusively own
    /// runtime-tree modifications during deployment.
    /// </summary>
    public static async Task<AfdevRuntimeValidation> ValidateAsync(MatchRuntimeOptions options,
        uint modeId, ushort mapId, CancellationToken cancellationToken = default)
    {
        options.ResolvePaths(AppContext.BaseDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.UsesReservedRuntime)
            AfdevSharedResources.ValidateStorage(options);

        var manifest = RuntimeManifest.Load(options.ManifestPath);
        var target = RoomTargetCatalog.Resolve(modeId, mapId);

        ValidateLayout(manifest, target);

        if (options.UsesReservedRuntime)
        {
            for (var slot = 0; slot < options.ReservedRuntimeRoots.Length; slot++)
            {
                var executable = RuntimePath.ResolveConfiguredPath(options.ReservedRuntimeRoots[slot], options.ReservedRuntimeExecutables[slot]);
                var relative = RuntimePath.NormalizeRelativePath(Path.GetRelativePath(options.ReservedRuntimeRoots[slot], executable));

                if (!manifest.Files.Any(file => file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"AFDEV manifest 未登记槽 {slot} 的 EXE：{relative}");

                var reservedIdentity = AfdevBinaryInspector.Inspect(executable);

                if (!HashEquals(reservedIdentity.AfdevSha256, manifest.AfdevSha256) ||
                    !HashEquals(reservedIdentity.ServerMoveV4Sha256, manifest.ServerMoveV4Sha256))
                    throw new InvalidDataException($"AFDEV 预留 EXE 或 ServerMove v4 指纹与 manifest 不一致：{executable}");
            }

            foreach (var root in options.ReservedRuntimeRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var resources = await AfdevReservedResources.AcquireAsync(root, manifest, cancellationToken);
            }

            return new AfdevRuntimeValidation(options, manifest, target, modeId, mapId);
        }

        var identity = AfdevBinaryInspector.Inspect(options.AfdevExecutable);

        if (!HashEquals(identity.AfdevSha256, manifest.AfdevSha256) ||
            !HashEquals(identity.ServerMoveV4Sha256, manifest.ServerMoveV4Sha256))
            throw new InvalidDataException("AFDEV 或 ServerMove v4 指纹与 manifest 不一致。");

        var actual = EnumerateRuntimeFiles(options.AfdevRuntimeRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!actual.SetEquals(manifest.Files.Select(file => RuntimePath.NormalizeRelativePath(file.RelativePath))))
            throw new InvalidDataException("AFDEV 运行目录与 manifest 文件集合不一致，存在缺失或额外文件。");

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var observed = await DescribeFileAsync(options.AfdevRuntimeRoot, file.RelativePath, cancellationToken);

            if (observed.Size != file.Size || !HashEquals(observed.Sha256, file.Sha256))
                throw new InvalidDataException($"AFDEV 资源长度或 SHA-256 不匹配：{file.RelativePath}");
        }

        return new AfdevRuntimeValidation(options, manifest, target, modeId, mapId);
    }

    /// <summary>
    /// Calculates a manifest for a prepared conservative runtime package. Original game hashes come from
    /// controlled build records; existing manifests are preserved.
    /// </summary>
    /// <remarks>
    /// Reads the runtime tree only. Manifest integrity and successful native loading are separate
    /// acceptance checks; this method neither trims resources nor installs patches.
    /// </remarks>
    public static async Task<RuntimeManifest> CreateManifestAsync(MatchRuntimeOptions options,
        string runtimeVersion, string gameBuildSha256, uint modeId, ushort mapId,
        CancellationToken cancellationToken = default)
    {
        options.ResolvePaths(AppContext.BaseDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(options.ManifestPath))
            throw new IOException("AFDEV manifest 已存在，请为新运行版本配置独立清单路径。");

        var target = RoomTargetCatalog.Resolve(modeId, mapId);
        var identity = AfdevBinaryInspector.Inspect(options.AfdevExecutable);
        var manifest = new RuntimeManifest
        {
            RuntimeVersion = runtimeVersion,
            GameBuildSha256 = gameBuildSha256,
            AfdevSha256 = identity.AfdevSha256,
            ServerMoveV4Sha256 = identity.ServerMoveV4Sha256,
            ModeMapCatalogVersion = RoomTargetCatalog.Version
        };

        var files = options.UsesReservedRuntime
            ? AfdevReservedRuntimeFiles.Enumerate(options.AfdevRuntimeRoot)
            : EnumerateRuntimeFiles(options.AfdevRuntimeRoot);

        foreach (var relativePath in files)
            manifest.Files.Add(await DescribeFileAsync(options.AfdevRuntimeRoot, relativePath, cancellationToken));

        manifest.ValidateMetadata();
        ValidateLayout(manifest, target);
        cancellationToken.ThrowIfCancellationRequested();
        manifest.Save(options.ManifestPath);

        return manifest;
    }

    internal static void ValidateLayout(RuntimeManifest manifest, RoomTarget target)
    {
        if (manifest.ModeMapCatalogVersion != RoomTargetCatalog.Version)
            throw new InvalidDataException("AFDEV manifest 的 mode/map 白名单版本与本程序不一致。");

        var paths = manifest.Files.Select(file => RuntimePath.NormalizeRelativePath(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] requiredFiles =
        [
            RuntimePath.AfdevRelativePath, "TGame/PCTOC.txt", "TGame/Config/DefaultGame.ini",
            "TGame/Config/DefaultEngine.ini", "TGame/Config/DefaultInput.ini",
            "TGame/Config/DefaultTGBioGame.ini", "TGame/Config/DefaultTGBio2Game.ini",
            "TGame/Config/DefaultTGBioSkill.ini", "TGame/Config/DefaultTGBioWeapon.ini",
            "TGame/Config/DefaultPZGame.ini", "TGame/Config/DefaultPZZombieSkill.ini",
            "TGame/Config/DefaultPZZombieWeapon.ini", "TGame/Config/DefaultSkill.ini",
            "TGame/Config/DefaultWeapon.ini", "TGame/Config/WeaponMatrix.bin",
            $"TGame/CookedPC/{target.GameClass.Split('.')[0]}.u"
        ];
        string[] requiredTrees =
        [
            "Engine/", "TGame/Data/", "TGame/Localization/INT/", "TGame/Movies/",
            "TGame/Splash/", "TGame/TDR/"
        ];

        foreach (var path in requiredFiles)
        {
            if (!paths.Contains(path))
                throw new InvalidDataException($"AFDEV 保守运行包缺少必要文件：{path}");
        }

        foreach (var tree in requiredTrees)
        {
            if (!paths.Any(path => path.StartsWith(tree, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"AFDEV 保守运行包缺少必要资源子树：{tree}");
        }

        manifest.ResolveMainMap(target.MapName);
    }

    private static IEnumerable<string> EnumerateRuntimeFiles(string root)
    {
        RuntimePath.RejectReparsePoints(root);

        var pending = new Stack<string>();

        pending.Push(root);

        // Avoid recursive enumeration that automatically follows directory links; check attributes before entering each level.
        while (pending.TryPop(out var directory))
        {
            RuntimePath.RejectReparsePoints(directory);

            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(path);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"AFDEV 运行树不允许重解析点：{path}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
                else
                    yield return RuntimePath.ValidateRuntimeFilePath(Path.GetRelativePath(root, path));
            }
        }
    }

    private static async Task<RuntimeManifestFile> DescribeFileAsync(string root, string relativePath,
        CancellationToken cancellationToken)
    {
        var path = RuntimePath.ResolveUnderRoot(root, relativePath);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);

        return new RuntimeManifestFile
        {
            RelativePath = RuntimePath.NormalizeRelativePath(relativePath),
            Size = size,
            Sha256 = Convert.ToHexStringLower(hash)
        };
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
