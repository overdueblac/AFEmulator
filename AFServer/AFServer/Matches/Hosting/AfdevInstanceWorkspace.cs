using System.Security.Cryptography;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Separate state and diagnostics for each attempt. Resources may be borrowed from reserved clients in
/// place; candidate ports do not establish native-session readiness.
/// </summary>
/// <remarks>
/// Different allocations may select the same candidate slot at this stage. AfdevRuntimeReservation owns
/// runtime exclusivity at actual startup.
/// </remarks>
public sealed partial record AfdevInstanceWorkspace(Guid AllocationId, ulong RoomId, uint ModeId, ushort MapId,
    string Directory, string WorkingDirectory, string ExecutablePath, int PublicPort, int TargetPort,
    string ManifestVersion)
{
    /// <summary>
    /// Physical-instance identity. AllocationId is empty during warmup and belongs to no room.
    /// </summary>
    public Guid InstanceId { get; init; } = AllocationId;

    /// <summary>
    /// The root containing actual native resources. In-place mode points to the reserved client; Directory
    /// always belongs to this attempt's diagnostics.
    /// </summary>
    public string RuntimeRoot { get; init; } = Directory;

    /// <summary>
    /// Whether resources belong to a reserved client. The host borrows them without rights to write, copy,
    /// or delete them.
    /// </summary>
    public bool UsesReservedRuntime { get; init; }

    private AfdevSharedResources? sharedResources;
    private AfdevReservedResources? reservedResources;

    /// <summary>
    /// Private-resource bytes actually copied for this attempt, excluding shared static packages.
    /// </summary>
    public long PrivateResourceBytes { get; private set; }

    /// <summary>
    /// Static-package bytes referenced through hard links without creating another data copy.
    /// </summary>
    public long SharedResourceBytes { get; private set; }

    /// <summary>
    /// Total time spent validating resources, protecting them, and publishing diagnostics for this attempt.
    /// </summary>
    public double PreparationMilliseconds { get; private set; }

    /// <summary>
    /// Borrows clients in place or prepares legacy isolated resources as configured, rechecks hashes, and
    /// atomically publishes diagnostics. Never reuses old diagnostics or runs AFDEV.
    /// </summary>
    /// <remarks>
    /// Cancellation or failure cleans up only this attempt's temporary directory. Never kills by old PID or
    /// writes ready/state files.
    /// </remarks>
    /// <exception cref="AfdevWorkspacePreparationException">
    /// Both preparation and cleanup failed; the caller must retain the exception and retry cleanup.
    /// </exception>
    public static async Task<AfdevInstanceWorkspace> PrepareAsync(AfdevRuntimeValidation validation,
        Guid allocationId, ulong roomId, int slot, CancellationToken cancellationToken = default)
        => await PrepareCoreAsync(validation, allocationId, roomId, slot, allocationId.ToString("N"), 0, Guid.Empty, cancellationToken, allocationId);

    /// <summary>
    /// Prepares a fresh diagnostic directory for a startup attempt without reusing prior identity or
    /// readiness files.
    /// </summary>
    /// <exception cref="AfdevWorkspacePreparationException">
    /// Both preparation and cleanup failed; retain this reservation until cleanup finishes.
    /// </exception>
    public static async Task<AfdevInstanceWorkspace> PrepareAttemptAsync(AfdevRuntimeValidation validation,
        Guid allocationId, ulong roomId, int slot, int attemptNumber, Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        if (attemptNumber is < 1 or > 4 || attemptId == Guid.Empty)
            throw new ArgumentException("启动尝试需要独立身份及 1–4 的序号。");

        // Paths use only allocation and attempt numbers. Keep the full attempt GUID in metadata to avoid legacy-engine path-concatenation overflow.
        var relativeDirectory = $"{allocationId:N}/{attemptNumber}";

        return await PrepareCoreAsync(validation, allocationId, roomId, slot, relativeDirectory, attemptNumber, attemptId, cancellationToken, allocationId);
    }

    /// <summary>
    /// Prepares independent diagnostics and the associated resource lease for an unbound physical host.
    /// Instance identity cannot serve as player allocation.
    /// </summary>
    public static Task<AfdevInstanceWorkspace> PreparePhysicalAttemptAsync(AfdevRuntimeValidation validation,
        Guid instanceId, int slot, int attemptNumber, Guid attemptId, CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty || attemptId == Guid.Empty || attemptNumber is < 1 or > 4)
            throw new ArgumentException("物理实例与启动尝试身份无效。");

        return PrepareCoreAsync(validation, Guid.Empty, 0, slot, $"{instanceId:N}/{attemptNumber}",
            attemptNumber, attemptId, cancellationToken, instanceId);
    }

    private static async Task<AfdevInstanceWorkspace> PrepareCoreAsync(AfdevRuntimeValidation validation,
        Guid allocationId, ulong roomId, int slot, string relativeDirectory, int attemptNumber, Guid attemptId,
        CancellationToken cancellationToken, Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var options = validation.Options;
        var manifest = validation.Manifest;
        var preparationTimer = System.Diagnostics.Stopwatch.StartNew();

        options.Validate();
        manifest.ValidateMetadata();

        if (instanceId == Guid.Empty || (allocationId == Guid.Empty) != (roomId == 0) || slot < 0 || slot >= options.MaxInstances)
            throw new ArgumentException("实例准备需要非空 allocation、有效房间和配置范围内的槽。");
        if (RoomTargetCatalog.Resolve(validation.ModeId, validation.MapId) != validation.Target)
            throw new InvalidDataException("实例目标与预检路由不一致。");

        cancellationToken.ThrowIfCancellationRequested();
        RuntimePath.RejectReparsePoints(options.InstancesRoot);
        System.IO.Directory.CreateDirectory(options.InstancesRoot);
        RuntimePath.RejectReparsePoints(options.InstancesRoot);

        var name = instanceId.ToString("N");
        var destination = RuntimePath.ResolveUnderRoot(options.InstancesRoot, relativeDirectory);
        var runtimeRoot = options.UsesReservedRuntime ? options.GetRuntimeRoot(slot) : destination;
        var lockPath = RuntimePath.ResolveUnderRoot(options.InstancesRoot, name + ".prepare.lock");

        // Besides Windows MAX_PATH, conservatively reserve 40 characters for UE3 relative-path concatenation and suffixes.
        // .NET long-path copying does not prove that the native loader can read the path. This deployment budget is not proof for every native path function.
        if (manifest.Files.Any(file => Path.Combine(runtimeRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)).Length >= 220))
            throw new InvalidDataException("AFDEV 实际资源路径超过原生 MAX_PATH 保守预算（须少于 220 字符），请核对预留客户端路径或缩短传统实例目录。");

        using var preparationLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);

        if (System.IO.Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"AFDEV 实例目录已存在，不能复用旧回合：{destination}");

        if (!options.UsesReservedRuntime)
            EnsureCopySpace(options, manifest);
        else if (new DriveInfo(Path.GetPathRoot(options.InstancesRoot)!).AvailableFreeSpace < options.MinimumFreeSpaceBytes)
            throw new IOException("AFDEV 原位运行诊断卷的可用空间低于配置保留量。");

        AfdevWorkspacePreparationException.OwnedStaging? stagingOwnership = null;

        try
        {
            stagingOwnership = AfdevWorkspacePreparationException.OwnedStaging.Create(options.InstancesRoot);

            if (options.UsesReservedRuntime)
                stagingOwnership.ReservedResources = await AfdevReservedResources.AcquireAsync(runtimeRoot, manifest, cancellationToken);
            else
                stagingOwnership.SharedResources = await AfdevSharedResources.AcquireAsync(validation, cancellationToken);

            var staging = stagingOwnership.Path;
            var shared = stagingOwnership.SharedResources;
            long privateBytes = 0;

            foreach (var file in options.UsesReservedRuntime ? [] : manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (AfdevSharedResources.IsShared(file))
                    shared!.LinkTo(staging, file, cancellationToken);
                else
                {
                    await CopyVerifiedAsync(options.AfdevRuntimeRoot, staging, file, cancellationToken);
                    privateBytes = checked(privateBytes + file.Size);
                }
            }

            System.IO.Directory.CreateDirectory(Path.Combine(staging, "logs"));
            manifest.Save(Path.Combine(staging, "runtime-manifest.json"));

            var metadata = new PrepareCoreAsyncJson1
            {
                state = "DEPLOYMENT_PREPARED", instance_id = instanceId, allocation_id = allocationId, room_id = roomId,
                attempt_number = attemptNumber, attempt_id = attemptId,
                mode_id = validation.ModeId, map_id = validation.MapId,
                map = validation.Target.MapName, game = validation.Target.GameClass,
                public_port = options.PublicPortBase + slot, target_port = options.TargetPortBase + slot,
                runtime_version = manifest.RuntimeVersion, afdev_sha256 = manifest.AfdevSha256,
                servermove_v4_sha256 = manifest.ServerMoveV4Sha256,
                mode_map_catalog_version = manifest.ModeMapCatalogVersion,
                storage_layout = options.UsesReservedRuntime ? "reserved-client-in-place-v1" : "private-files-protected-shared-packages-v1",
                runtime_root = runtimeRoot,
                executable_path = options.UsesReservedRuntime ? options.GetRuntimeExecutable(slot)
                    : Path.Combine(runtimeRoot, RuntimePath.AfdevRelativePath),
                private_resource_bytes = privateBytes, shared_resource_bytes = shared?.SharedBytes ?? 0,
                shared_file_count = shared?.SharedFileCount ?? 0
            };
            var metadataPath = RuntimePath.ResolveUnderRoot(staging, "allocation.json");

            await using (var stream = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeAsync(stream, metadata, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            RuntimePath.RejectReparsePoints(destination);
            RuntimePath.RejectReparsePoints(staging);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var directoryIdentity = ReadDirectoryIdentity(staging);

            System.IO.Directory.Move(staging, destination);
            stagingOwnership.CompleteMove();

            var workspace = new AfdevInstanceWorkspace(allocationId, roomId, validation.ModeId, validation.MapId,
                destination, Path.Combine(runtimeRoot, "Binaries", "Win32"),
                options.UsesReservedRuntime ? options.GetRuntimeExecutable(slot)
                    : Path.Combine(runtimeRoot, RuntimePath.AfdevRelativePath),
                options.PublicPortBase + slot, options.TargetPortBase + slot, manifest.RuntimeVersion)
            {
                InstanceId = instanceId, RuntimeRoot = runtimeRoot, UsesReservedRuntime = options.UsesReservedRuntime
            };

            workspace.ownedDirectory = destination;
            workspace.ownedDirectoryIdentity = directoryIdentity;
            workspace.sharedResources = shared;
            workspace.reservedResources = stagingOwnership.ReservedResources;
            workspace.PrivateResourceBytes = privateBytes;
            workspace.SharedResourceBytes = shared?.SharedBytes ?? 0;
            workspace.PreparationMilliseconds = preparationTimer.Elapsed.TotalMilliseconds;

            return workspace;
        }
        catch (Exception failure)
        {
            if (stagingOwnership != null)
            {
                try { stagingOwnership.Cleanup(); }
                catch (Exception cleanupFailure)
                {
                    throw stagingOwnership.CreateFailure(failure, cleanupFailure);
                }
            }

            throw;
        }
    }

    private static async Task CopyVerifiedAsync(string sourceRoot, string destinationRoot, RuntimeManifestFile file,
        CancellationToken cancellationToken)
    {
        var relativePath = RuntimePath.ValidateRuntimeFilePath(file.RelativePath);
        var source = RuntimePath.ResolveUnderRoot(sourceRoot, relativePath);
        var destination = RuntimePath.ResolveUnderRoot(destinationRoot, relativePath);

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RuntimePath.RejectReparsePoints(destination);

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long copied = 0;

        if (input.Length != file.Size)
            throw new InvalidDataException($"AFDEV 资源在预检后改变长度：{relativePath}");

        int count;

        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            copied += count;
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        if (copied != file.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()),
            file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"AFDEV 复制内容与预检清单不一致：{relativePath}");
    }
}

/// <summary>
/// Preparation failed and its owned temporary directory remains. The host must retain the slot and retry
/// cleanup before reuse.
/// </summary>
/// <remarks>
/// Only the operation that actually created the temporary directory may create this instance. Cancellation
/// and copy failure follow the same ownership rules.
/// </remarks>
public sealed class AfdevWorkspacePreparationException : IOException
{
    private readonly OwnedStaging ownership;

    private AfdevWorkspacePreparationException(OwnedStaging ownership, Exception failure, Exception cleanupFailure)
        : base($"AFDEV 准备失败且临时目录尚待清理：{ownership.Path}", new AggregateException(failure, cleanupFailure))
    {
        this.ownership = ownership;
    }

    /// <summary>
    /// The instance root that created this temporary directory, for diagnostics only; it cannot change
    /// cleanup scope.
    /// </summary>
    public string Root => ownership.Root;

    /// <summary>
    /// The temporary directory actually created and still owned by this preparation operation, for
    /// diagnostics only.
    /// </summary>
    public string StagingPath => ownership.Path;

    /// <summary>
    /// Retries deletion of this operation's own temporary directory. Failure retains ownership; repeated
    /// calls after success no longer touch the filesystem.
    /// </summary>
    public void Cleanup() => ownership.Cleanup();

    /// <summary>
    /// Ownership begins only after the factory creates the directory, preventing arbitrary paths from
    /// becoming deletable objects.
    /// </summary>
    internal sealed class OwnedStaging
    {
        private readonly object gate = new();
        private string? directoryIdentity;
        private bool released;

        private OwnedStaging(string root, string path)
        {
            Root = root;
            Path = path;
        }

        internal string Root { get; }
        internal string Path { get; }
        internal AfdevSharedResources? SharedResources { get; set; }
        internal AfdevReservedResources? ReservedResources { get; set; }

        internal static OwnedStaging Create(string root)
        {
            var ownedRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
            var staging = RuntimePath.ResolveUnderRoot(ownedRoot, ".preparing-" + Guid.NewGuid().ToString("N"));

            if (System.IO.Directory.Exists(staging) || File.Exists(staging))
                throw new IOException("AFDEV 临时目录已存在，准备已停止。");

            System.IO.Directory.CreateDirectory(staging);

            var ownership = new OwnedStaging(ownedRoot, staging);

            try { ownership.directoryIdentity = AfdevInstanceWorkspace.ReadDirectoryIdentity(staging); }
            catch (Exception failure)
            {
                // The empty directory exists but identity lookup failed. Retain the same object and reservation; never lose the path or gain deletion rights through a new retry-time identity.
                throw ownership.CreateFailure(failure, new IOException("临时目录身份未能建立，请人工核查空目录；在目录消失前保留占用。"));
            }

            return ownership;
        }

        internal void CompleteMove()
        {
            lock (gate) released = true;
        }

        internal AfdevWorkspacePreparationException CreateFailure(Exception failure, Exception cleanupFailure) =>
            new(this, failure, cleanupFailure);

        internal void Cleanup()
        {
            lock (gate)
            {
                if (released) return;

                DeleteStaging();
                SharedResources?.Dispose();
                SharedResources = null;
                ReservedResources?.Dispose();
                ReservedResources = null;
                released = true;
            }
        }

        private void DeleteStaging()
        {
            if (!System.IO.Path.IsPathFullyQualified(Path) || !RuntimePath.IsWithinRoot(Root, Path) ||
                !string.Equals(System.IO.Path.GetDirectoryName(Path), Root, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(Path).StartsWith(".preparing-", StringComparison.Ordinal))
                throw new IOException("拒绝清理不属于本次准备的临时路径。");

            RuntimePath.RejectReparsePoints(Path);

            if (File.Exists(Path)) throw new IOException("AFDEV 自有临时目录被替换成文件，不能释放所有权。");
            if (!System.IO.Directory.Exists(Path)) return;

            if (!string.Equals(AfdevInstanceWorkspace.ReadDirectoryIdentity(Path), directoryIdentity, StringComparison.Ordinal))
                throw new IOException("AFDEV 临时目录的文件系统身份已改变，拒绝清理未知目录。");

            var pending = new Stack<string>();

            pending.Push(Path);

            while (pending.TryPop(out var directory))
            {
                RuntimePath.RejectReparsePoints(directory);

                foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory))
                {
                    RuntimePath.RejectReparsePoints(path);

                    if (System.IO.Directory.Exists(path)) pending.Push(path);
                }
            }

            System.IO.Directory.Delete(Path, recursive: true);
        }
    }
}
