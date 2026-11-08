namespace AFServer.AssaultFire.Matches;

/// <summary>
/// AFDEV package and instance deployment settings. Resolve paths against the executable directory, not the
/// working directory.
/// </summary>
public sealed class MatchRuntimeOptions
{
    /// <summary>
    /// Fills ordinary BIO human vacancies from frozen participant capacity. Disabled by default and read at
    /// startup; the roster remains separate from authenticated sessions.
    /// </summary>
    public AfdevBioBotOptions BioBots { get; set; } = new();

    /// <summary>Ordinary team matches use TGame's own AI independently of ordinary BIO scheduling.</summary>
    public AfdevPvpBotOptions TeamBots { get; set; } = new();

    /// <summary>
    /// Bomb matches use TGame's TGBMBot and planting/defusing AI, filling frozen vacancies by default.
    /// </summary>
    public AfdevPvpBotOptions BombBots { get; set; } = new();

    /// <summary>Free-for-all matches use native TGame TGBot, filling frozen vacancies by default.</summary>
    public AfdevPvpBotOptions IndividualBots { get; set; } = new();

    /// <summary>
    /// Read-only original runtime root. With reserved clients, resolves to the first reserved root for
    /// creating the shared-resource manifest.
    /// </summary>
    public string AfdevRuntimeRoot { get; set; } = "AFGameRuntime";

    /// <summary>
    /// Client roots used in place by slot. Slots may share a root but must select distinct AFDEV
    /// executables.
    /// </summary>
    public string[] ReservedRuntimeRoots { get; set; } = [];

    /// <summary>
    /// Executable paths relative to each reserved client root. An empty array preserves legacy
    /// configuration, using TGame_AFDEV.exe for every slot.
    /// </summary>
    public string[] ReservedRuntimeExecutables { get; set; } = [];

    /// <summary>
    /// Whether to run reserved clients directly. Instance directories hold only this attempt's diagnostics
    /// and own no client resources.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesReservedRuntime => ReservedRuntimeRoots is { Length: > 0 };

    /// <summary>Prebuilt x86 AFDEV path relative to the runtime root, absolute after resolution.</summary>
    public string AfdevExecutable { get; set; } = RuntimePath.AfdevRelativePath;

    /// <summary>Resource-manifest path outside the runtime root.</summary>
    public string ManifestPath { get; set; } = "AFGameRuntime.manifest.json";

    /// <summary>
    /// Common parent for physical-instance diagnostics. In-place mode writes diagnostics only; legacy
    /// shared mode also requires the runtime package's volume.
    /// </summary>
    public string InstancesRoot { get; set; } = "runtime/matches";

    /// <summary>Maximum number of retained concurrent instances.</summary>
    public int MaxInstances { get; set; } = 4;

    /// <summary>
    /// Number of unbound warm engines retained in the background, defaulting to one. Active matches consume
    /// separate capacity.
    /// </summary>
    public int WarmInstances { get; set; } = 1;

    /// <summary>First externally advertised UDP port.</summary>
    public int PublicPortBase { get; set; } = 65008;

    /// <summary>First upstream AFDEV UDP port.</summary>
    public int TargetPortBase { get; set; } = 7777;

    /// <summary>Maximum readiness wait in seconds per startup attempt.</summary>
    public double StartupTimeoutSeconds { get; set; } = 90;

    /// <summary>Maximum startup attempts, including the first attempt.</summary>
    public int MaxStartupAttempts { get; set; } = 3;

    /// <summary>
    /// Disk space reserved on the instance volume. In-place mode counts no client copy; legacy mode
    /// additionally counts private resources.
    /// </summary>
    public long MinimumFreeSpaceBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Resolves paths against the specified executable directory and validates settings. Repeated calls do
    /// not prepend a base to already absolute paths.
    /// </summary>
    public void ResolvePaths(string baseDirectory)
    {
        ValidateValues();

        var paths = GetPaths(baseDirectory);

        ValidatePaths(paths);
        AfdevRuntimeRoot = paths.RuntimeRoot;
        AfdevExecutable = paths.Executable;
        ManifestPath = paths.Manifest;
        InstancesRoot = paths.Instances;
        ReservedRuntimeRoots = paths.ReservedRoots;
        ReservedRuntimeExecutables = paths.ReservedExecutables;
    }

    /// <summary>
    /// Validates numbers, directory isolation, fixed AFDEV locations, and reparse points in existing paths.
    /// Does not establish launchability.
    /// </summary>
    public void Validate()
    {
        ValidateValues();
        ValidatePaths(GetPaths(AppContext.BaseDirectory));
    }

    private void ValidateValues()
    {
        if (TeamBots is null || BombBots is null || IndividualBots is null ||
            TeamBots.Difficulty > 3 || BombBots.Difficulty > 3 || IndividualBots.Difficulty > 3)
            throw new InvalidDataException("MatchRuntime 的 TeamBots、BombBots、IndividualBots 不能为 null，Difficulty 必须为 0–3。");

        if (BioBots is null)
            throw new InvalidDataException("MatchRuntime.BioBots 不能为 null。");

        if (ReservedRuntimeRoots is null ||
            (ReservedRuntimeRoots.Length > 0 && MaxInstances > ReservedRuntimeRoots.Length))
            throw new InvalidDataException("MatchRuntime 预留客户端目录不能为 null，且必须为每个实例槽提供目录。");

        if (ReservedRuntimeExecutables is null ||
            (ReservedRuntimeExecutables.Length > 0 && ReservedRuntimeExecutables.Length != ReservedRuntimeRoots.Length))
            throw new InvalidDataException("MatchRuntime 预留 EXE 必须为空数组或与预留客户端目录逐槽对应。");

        if (WarmInstances < 1 || WarmInstances > MaxInstances)
            throw new InvalidDataException("MatchRuntime 预热实例数必须在 1 与最大实例数之间。");

        if (MinimumFreeSpaceBytes < 64L * 1024 * 1024)
            throw new InvalidDataException("MatchRuntime 磁盘保留空间不得少于 64 MiB。");

        if (MaxInstances is < 1 or > 256 || PublicPortBase < 1024 || TargetPortBase < 1024 ||
            (long)PublicPortBase + MaxInstances > 65536 || (long)TargetPortBase + MaxInstances > 65536)
            throw new InvalidDataException("MatchRuntime 实例数或 UDP 端口范围无效。");
        if (PublicPortBase < (long)TargetPortBase + MaxInstances &&
            TargetPortBase < (long)PublicPortBase + MaxInstances)
            throw new InvalidDataException("MatchRuntime 对外端口与 AFDEV 上游端口范围不能重叠。");
        if (!double.IsFinite(StartupTimeoutSeconds) || StartupTimeoutSeconds < 5 ||
            StartupTimeoutSeconds > TimeSpan.MaxValue.TotalSeconds || MaxStartupAttempts is < 1 or > 4)
            throw new InvalidDataException("MatchRuntime 启动超时或最大尝试次数无效。");
    }

    /// <summary>
    /// Returns the resolved client root for this slot. Multiple slots may borrow one read-only resource
    /// set.
    /// </summary>
    internal string GetRuntimeRoot(int slot)
    {
        if (slot < 0 || slot >= MaxInstances)
            throw new ArgumentOutOfRangeException(nameof(slot));

        return UsesReservedRuntime ? ReservedRuntimeRoots[slot] : AfdevRuntimeRoot;
    }

    /// <summary>
    /// Returns the slot's absolute executable path. Reserved slots select AFDEV or AFDEV2 from
    /// configuration; legacy instances still use AFDEV.
    /// </summary>
    public string GetRuntimeExecutable(int slot)
    {
        var root = GetRuntimeRoot(slot);
        var executable = UsesReservedRuntime && ReservedRuntimeExecutables.Length > 0
            ? ReservedRuntimeExecutables[slot] : RuntimePath.AfdevRelativePath;

        return RuntimePath.ResolveConfiguredPath(root, executable);
    }

    private (string RuntimeRoot, string Executable, string Manifest, string Instances, string[] ReservedRoots, string[] ReservedExecutables) GetPaths(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathFullyQualified(baseDirectory))
            throw new InvalidDataException("MatchRuntime 必须由绝对 EXE 目录解析路径。");

        var reservedRoots = ReservedRuntimeRoots.Select(root => RuntimePath.ResolveConfiguredPath(baseDirectory, root)).ToArray();
        var reservedExecutables = reservedRoots.Select((root, slot) => RuntimePath.ResolveConfiguredPath(root,
            ReservedRuntimeExecutables.Length == 0 ? RuntimePath.AfdevRelativePath : ReservedRuntimeExecutables[slot])).ToArray();
        var runtimeRoot = reservedRoots.Length == 0
            ? RuntimePath.ResolveConfiguredPath(baseDirectory, AfdevRuntimeRoot)
            : reservedRoots[0];
        var executable = RuntimePath.ResolveConfiguredPath(runtimeRoot, AfdevExecutable);
        var manifest = RuntimePath.ResolveConfiguredPath(baseDirectory, ManifestPath);
        var instances = RuntimePath.ResolveConfiguredPath(baseDirectory, InstancesRoot);
        var expectedExecutable = RuntimePath.ResolveUnderRoot(runtimeRoot, RuntimePath.AfdevRelativePath);

        if (!string.Equals(executable, expectedExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"MatchRuntime 仅允许预制 x86 {RuntimePath.AfdevRelativePath}。");

        return (runtimeRoot, executable, manifest, instances, reservedRoots, reservedExecutables);
    }

    private static void ValidatePaths((string RuntimeRoot, string Executable, string Manifest, string Instances, string[] ReservedRoots, string[] ReservedExecutables) paths)
    {
        var roots = paths.ReservedRoots.Length == 0 ? new[] { paths.RuntimeRoot } : paths.ReservedRoots;

        for (var index = 0; index < roots.Length; index++)
        {
            var root = roots[index];
            var executable = paths.ReservedRoots.Length == 0 ? paths.Executable : paths.ReservedExecutables[index];

            if (!RuntimePath.IsAfdevExecutablePath(Path.GetRelativePath(root, executable)))
                throw new InvalidDataException("AFDEV 预留槽仅允许 Binaries/Win32 下的 TGame_AFDEV.exe 或 TGame_AFDEV2.exe。");

            if (RuntimePath.IsWithinRoot(root, paths.Instances) || RuntimePath.IsWithinRoot(paths.Instances, root))
                throw new InvalidDataException("AFDEV 客户端运行根与实例诊断根不能相同或互相包含。");
            if (RuntimePath.IsWithinRoot(root, paths.Manifest))
                throw new InvalidDataException("AFDEV manifest 必须位于客户端运行根外。");

            for (var other = 0; other < index; other++)
            {
                if (string.Equals(root, roots[other], StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(executable, paths.ReservedExecutables[other], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("AFDEV 共用客户端根时，各槽必须使用不同 EXE。");
                }
                else if (RuntimePath.IsWithinRoot(root, roots[other]) || RuntimePath.IsWithinRoot(roots[other], root))
                    throw new InvalidDataException("AFDEV 预留客户端根不能互相包含。");
            }

            RuntimePath.RejectReparsePoints(root);
            RuntimePath.RejectReparsePoints(executable);
        }

        if (RuntimePath.IsWithinRoot(paths.Instances, paths.Manifest))
            throw new InvalidDataException("AFDEV manifest 必须位于实例诊断根外。");

        RuntimePath.RejectReparsePoints(paths.RuntimeRoot);
        RuntimePath.RejectReparsePoints(paths.Executable);
        RuntimePath.RejectReparsePoints(paths.Manifest);
        RuntimePath.RejectReparsePoints(paths.Instances);
    }
}
