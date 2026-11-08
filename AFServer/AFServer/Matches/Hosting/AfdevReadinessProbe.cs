namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Native-read evidence from the same process and startup attempt. A ready boolean in a file cannot create
/// it.
/// </summary>
public sealed record AfdevNativeEvidence(Guid AllocationId, Guid AttemptId, uint ProcessId,
    DateTimeOffset ProcessCreationTimeUtc, int PublicPort, int TargetPort, uint ModeId, ushort MapId,
    string MapName, string GameClass, string ManifestVersion, string AfdevSha256, string ServerMoveV4Sha256,
    uint LoadMapStage, uint LoadMapStageMask, uint SetGameInfoCalls, uint World, uint WorldPackage,
    uint WorldInfo, uint GameInfo, uint GameInfoClass, uint GameInfoPackage, uint NetDriver, uint Socket,
    int RekeyCount, string AesScheduleSha256, DateTimeOffset ObservedAtUtc, uint NativeSocketHandle, int NativeSocketPort,
    string NativeSocketAddress = "", Guid InstanceId = default, long WorldGeneration = 1)
{
    /// <summary>
    /// Participant capacity read from native MaxPlayers and checked against this OPEN's arguments.
    /// </summary>
    public byte FighterCapacity { get; init; }
}

/// <summary>
/// Strictly reads the post-OPEN map, actual GameInfo, complete v4 patch, and current live socket. Never
/// reads historical ready files.
/// </summary>
/// <remarks>
/// Calls must be serialized; cancellation requires stopping this attempt's process first. Never operate on
/// the same main thread concurrently with initialization or StopAsync.
/// </remarks>
public sealed class AfdevReadinessProbe
{
    private readonly AfdevNativeBootstrap bootstrap;
    private readonly AfdevRuntimeReservation reservation;
    private uint lastSocket;
    private int rekeyCount;

    /// <summary>
    /// Binds the reader to this native startup and the reservation used for actual port handoff.
    /// </summary>
    public AfdevReadinessProbe(AfdevNativeBootstrap bootstrap, AfdevRuntimeReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(reservation);

        if (bootstrap.Workspace.InstanceId != reservation.InstanceId ||
            bootstrap.Workspace.PublicPort != reservation.PublicPort || bootstrap.Workspace.TargetPort != reservation.TargetPort)
            throw new ArgumentException("原生 readiness 必须使用同一物理实例的精确端口预约。");

        this.bootstrap = bootstrap;
        this.reservation = reservation;
    }

    /// <summary>The first unmet current condition. Timeout never becomes partial success.</summary>
    public string WaitingCondition => bootstrap.WaitingCondition;

    /// <summary>
    /// Allows bounded resampling only when Actors-array relocation is the first failure in this complete
    /// check. Exceptions and other failures cannot retry.
    /// </summary>
    internal bool RetryableActorArrayChange { get; private set; }

    /// <summary>
    /// Continuously reads this native instance and returns only when every condition holds simultaneously.
    /// Cancellation or exit ends the attempt directly.
    /// </summary>
    public async Task<AfdevNativeEvidence> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var evidence = await CheckAsync(cancellationToken);

            if (evidence != null) return evidence;

            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>
    /// Revalidates the current world and socket, calling native AES_SetKey again after socket recreation.
    /// Returns null until ready.
    /// </summary>
    public Task<AfdevNativeEvidence?> CheckAsync(CancellationToken cancellationToken)
        => CheckCoreAsync(null, cancellationToken);

    /// <summary>
    /// Health checks preserve the original binding. Resample Actors-array relocation at most three times;
    /// reject a new world before socket querying or rekeying.
    /// </summary>
    internal async Task<AfdevNativeEvidence?> CheckAsync(AfdevNativeEvidence expected, CancellationToken cancellationToken)
    {
        for (int sample = 0; sample < 3; sample++)
        {
            var evidence = await CheckCoreAsync(expected, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (evidence is not null || !RetryableActorArrayChange) return evidence;
        }

        return null;
    }

    private async Task<AfdevNativeEvidence?> CheckCoreAsync(AfdevNativeEvidence? expected, CancellationToken cancellationToken)
    {
        RetryableActorArrayChange = false;
        cancellationToken.ThrowIfCancellationRequested();
        AfdevNativeBootstrap.Require(bootstrap.Initialized, "readiness 不能先于本次主线程 OPEN。");

        var process = bootstrap.Process;
        var workspace = bootstrap.Workspace;
        var validation = bootstrap.Validation;
        var manifest = validation.Manifest;
        var target = validation.Target;

        if (expected is not null)
            AfdevNativeBootstrap.Require(expected.AllocationId != Guid.Empty &&
                expected.InstanceId == workspace.InstanceId && expected.AttemptId == bootstrap.AttemptId &&
                expected.WorldGeneration == 1 && expected.ProcessId == process.ProcessId &&
                expected.ProcessCreationTimeUtc == process.CreationTimeUtc &&
                expected.PublicPort == workspace.PublicPort && expected.TargetPort == workspace.TargetPort &&
                expected.FighterCapacity == bootstrap.FighterCapacity &&
                expected.ModeId == validation.ModeId && expected.MapId == validation.MapId &&
                expected.MapName == target.MapName && expected.GameClass == target.GameClass,
                "健康核验证据不属于当前物理实例、进程、尝试和首次世界。");

        if (!process.IsAlive) throw new IOException("AFDEV 在原生 readiness 检查期间退出。");

        bootstrap.VerifyNoNativeFailure();
        bootstrap.VerifyDedicatedMode();
        bootstrap.VerifyServerMove();
        bootstrap.VerifyInstalledCode();

        uint stage = bootstrap.Stage;
        uint mask = bootstrap.StageMask;

        if (stage != 7 || mask != 0x7F)
            return Waiting($"post-OPEN LoadMap stage 7 (stage={stage}, observed_mask=0x{mask:X2})");

        uint engine = Read(AfdevNativeBootstrap.GEngine);

        if (engine == 0 || Read(engine + 0x678) != 0)
            return Waiting("OPEN PendingURL consumption");

        uint world = Read(AfdevNativeBootstrap.GWorld);

        if (expected is not null && world != expected.World)
            return Waiting($"bound GWorld changed expected=0x{expected.World:X8} actual=0x{world:X8}");

        if (world == 0 || bootstrap.SetGameInfoCount == 0 || bootstrap.SetGameInfoWorld != world)
            return Waiting($"post-OPEN SetGameInfo for current GWorld (world=0x{world:X8}, traced=0x{bootstrap.SetGameInfoWorld:X8}, calls={bootstrap.SetGameInfoCount})");

        uint package = bootstrap.Outermost(world);

        if (!bootstrap.MatchesName(package, target.MapName))
            return Waiting($"live map package {target.MapName} (package=0x{package:X8}, name_index=0x{Read(package + 0x2C):X8})");

        // Measured UWorld::GetWorldInfo(0) read path: PersistentLevel(+50) -> Actors(+3C) -> first actor.
        // Machine code was validated during suspended preparation. Recheck GWorld/Level around reads to reject intermediate travel state.
        uint level = Read(world + 0x50);

        if (level == 0 || Read(level + 0x40) == 0)
            return Waiting("live persistent level WorldInfo actor");

        uint actors = Read(level + 0x3C);
        uint worldInfo = actors == 0 ? 0 : Read(actors);
        uint gameInfo = worldInfo == 0 ? 0 : Read(worldInfo + 0x414);

        if (expected is not null && gameInfo != expected.GameInfo)
            return Waiting($"bound GameInfo changed expected=0x{expected.GameInfo:X8} actual=0x{gameInfo:X8}");

        uint gameClass = gameInfo == 0 ? 0 : Read(gameInfo + 0x34);

        if (gameClass == 0)
            return Waiting("live authoritative WorldInfo.Game and GameInfo.Class");

        uint gamePackage = bootstrap.Outermost(gameClass);
        string[] expectedGameClass = target.GameClass.Split('.');

        if (expectedGameClass.Length != 2 || !bootstrap.MatchesName(gameClass, expectedGameClass[1]) ||
            !bootstrap.MatchesName(gamePackage, expectedGameClass[0]))
            return Waiting($"live GameInfo class {target.GameClass} (class=0x{gameClass:X8}, name_index=0x{Read(gameClass + 0x2C):X8})");
        if (bootstrap.Outermost(gameInfo) != package || bootstrap.Outermost(worldInfo) != package)
            return Waiting("authoritative GameInfo and WorldInfo belonging to current map package");

        uint driver = Read(world + 0xD8);

        if (expected is not null && driver != expected.NetDriver)
            return Waiting($"bound NetDriver changed expected=0x{expected.NetDriver:X8} actual=0x{driver:X8}");

        uint socket = driver == 0 ? 0 : Read(driver + 0x1A8);

        if (socket == 0)
            return Waiting($"live encrypted socket creation (world=0x{world:X8}, net_driver=0x{driver:X8})");
        if (Read(socket + 0x18) != 1)
            return Waiting($"live encrypted socket crypto initialization (socket=0x{socket:X8}, enabled={Read(socket + 0x18)})");

        AfdevNativeBootstrap.Require(Read(socket) == 0x01CD4210, "live socket 不是已核实的 PH encrypted socket 类型。");
        bootstrap.VerifyListenPortInitialization(driver);

        bootstrap.WaitingCondition = "live socket getsockname on owned remote thread";

        var binding = await bootstrap.GetSocketPortAsync(socket, cancellationToken);

        // The main thread runs during OS queries. Reject changed objects first, then rekey the current socket through the engine.
        if (Read(AfdevNativeBootstrap.GWorld) != world || Read(world + 0xD8) != driver ||
            Read(driver + 0x1A8) != socket || Read(socket + 0x14) != binding.Handle)
            return Waiting("stable native world/driver/socket after remote getsockname");

        if (binding.Port != workspace.TargetPort)
            return Waiting($"actual encrypted socket target port (observed={binding.Port}, expected={workspace.TargetPort})");

        AfdevNativeBootstrap.Require(binding.Address.Equals(System.Net.IPAddress.Loopback),
            $"AFDEV 上游必须仅监听 127.0.0.1，实际 getsockname 为 {binding.Address}。");

        string scheduleHash = AfdevNativeBootstrap.Hash(process.ReadMemory(socket + 0x1C, 0x204));

        if (socket != lastSocket || scheduleHash != AfdevNativeBootstrap.ZeroScheduleSha256)
        {
            bootstrap.WaitingCondition = "live socket zero-key AES_SetKey completion";
            await bootstrap.RekeyAsync(socket, cancellationToken);
            lastSocket = socket;
            rekeyCount++;
            scheduleHash = AfdevNativeBootstrap.Hash(process.ReadMemory(socket + 0x1C, 0x204));
        }

        if (!reservation.VerifyTargetOwner(process, requireLoopback: true))
            return Waiting("Windows UDP target port ownership by this exact process");

        string? firstMismatch = null;

        bool Changed(uint actual, uint expectedValue, string field, bool actorArrayChange = false)
        {
            if (actual == expectedValue) return false;

            firstMismatch = $"{field} expected=0x{expectedValue:X8} actual=0x{actual:X8}";
            RetryableActorArrayChange = actorArrayChange;

            return true;
        }

        bool HashChanged(string actual, string expectedValue, string field)
        {
            if (actual == expectedValue) return false;

            firstMismatch = $"{field} expected_sha256={expectedValue} actual_sha256={actual}";

            return true;
        }

        // Rekey must preserve the same world, driver, and socket; never reuse evidence across travel or socket recreation.
        // Preserve strict short-circuit order: never dereference old pointers after upstream changes. Record only the first mismatch without extra native-object reads.
        if (Changed(Read(AfdevNativeBootstrap.GEngine), engine, "GEngine") ||
            Changed(Read(engine + 0x678), 0, "PendingURL.Count") ||
            Changed(Read(AfdevNativeBootstrap.GWorld), world, "GWorld") ||
            Changed(Read(world + 0x50), level, "World.PersistentLevel") ||
            Changed(Read(level + 0x3C), actors, "PersistentLevel.Actors.Data", actorArrayChange: true) ||
            Changed(Read(actors), worldInfo, "PersistentLevel.Actors[0]") ||
            Changed(Read(world + 0xD8), driver, "World.NetDriver") ||
            Changed(Read(driver + 0x1A8), socket, "NetDriver.Socket") ||
            Changed(Read(worldInfo + 0x414), gameInfo, "WorldInfo.Game") ||
            Changed(Read(gameInfo + 0x34), gameClass, "GameInfo.Class") ||
            Changed(Read(socket + 0x14), binding.Handle, "Socket.Handle") ||
            Changed(Read(socket + 0x18), 1, "Socket.CryptoEnabled") ||
            Changed(bootstrap.Stage, 7, "LoadMap.Stage") || Changed(bootstrap.StageMask, 0x7F, "LoadMap.StageMask") ||
            HashChanged(scheduleHash, AfdevNativeBootstrap.ZeroScheduleSha256, "Socket.AesScheduleAfterRekey") ||
            HashChanged(AfdevNativeBootstrap.Hash(process.ReadMemory(socket + 0x1C, 0x204)), scheduleHash, "Socket.AesScheduleFinal"))
            return Waiting($"stable native world/GameInfo/socket snapshot after rekey; first_mismatch={firstMismatch}");

        bootstrap.VerifyNoNativeFailure();
        bootstrap.VerifyListenPortInitialization(driver);
        bootstrap.VerifyFighterCapacity(gameInfo);
        bootstrap.WaitingCondition = "SESSION_READY";
        bootstrap.CaptureReadinessDiagnostic("strict-ready", force: true);

        return new AfdevNativeEvidence(workspace.AllocationId, bootstrap.AttemptId, process.ProcessId,
            process.CreationTimeUtc, workspace.PublicPort, workspace.TargetPort, validation.ModeId, validation.MapId,
            target.MapName, target.GameClass, manifest.RuntimeVersion, manifest.AfdevSha256,
            manifest.ServerMoveV4Sha256, stage, mask, bootstrap.SetGameInfoCount, world, package, worldInfo,
            gameInfo, gameClass, gamePackage, driver, socket, rekeyCount, scheduleHash, DateTimeOffset.UtcNow,
            binding.Handle, binding.Port, binding.Address.ToString(), workspace.InstanceId)
        {
            FighterCapacity = bootstrap.FighterCapacity
        };
    }

    private uint Read(uint address) => bootstrap.ReadUInt32(address);

    private AfdevNativeEvidence? Waiting(string condition)
    {
        bootstrap.ReportWaiting(condition);

        return null;
    }
}
