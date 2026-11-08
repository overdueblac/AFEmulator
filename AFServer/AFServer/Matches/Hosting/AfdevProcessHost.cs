using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// The actual native host for a validated runtime package. Serially owns preparation, startup, readiness,
/// supervision, and retryable cleanup.
/// </summary>
/// <remarks>
/// Each object accepts only one StartAsync or WarmAsync. If failure cleanup remains incomplete, retain the
/// object and retry StopAsync without releasing its reservation.
/// </remarks>
public sealed partial class AfdevProcessHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter<AfdevRuntimeState>() }
    };
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object journalGate = new();
    private readonly CancellationTokenSource stopRequested = new();
    private readonly AfdevRuntimeValidation validation;
    private AfdevRuntimeValidation? boundValidation;
    private readonly Action<string>? log;
    private Guid allocationId;
    private ulong roomId;
    private readonly int slot;
    private readonly int maxAttempts;
    private readonly TimeSpan startupTimeout;
    private AfdevRuntimeReservation? reservation;
    private AfdevOwnedProcess? process;
    private AfdevInstanceWorkspace? workspace;
    private AfdevReadinessProbe? probe;
    private AfdevNativeBootstrap? bootstrap;
    private AfdevWorkspacePreparationException? pendingPreparation;
    private Guid attemptId;
    private int attemptNumber;
    private bool startAccepted;
    private bool attemptCleaned;
    private uint processId;
    private DateTimeOffset? processCreatedUtc;
    private string manifestSha256 = "";

    /// <summary>
    /// Creates a host from a unique preflight result and allocation. External callers must not modify
    /// runtime resources or settings during the host lifetime.
    /// </summary>
    public AfdevProcessHost(AfdevRuntimeValidation validation, Guid allocationId, ulong roomId, int slot,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(validation);
        validation.Options.Validate();

        if ((allocationId == Guid.Empty) != (roomId == 0) || slot < 0 || slot >= validation.Options.MaxInstances)
            throw new ArgumentException("AFDEV 宿主需要成对的 allocation/房间身份及有效槽。");

        InstanceId = Guid.NewGuid();
        this.validation = validation;
        this.allocationId = allocationId;
        this.roomId = roomId;
        this.slot = slot;
        this.log = log;
        botBridgeEnabled = validation.Options.BioBots.Enabled;
        recordPlayerRoutes = validation.Options.BioBots.RecordPlayerRoutes;
        maxAttempts = validation.Options.MaxStartupAttempts;
        startupTimeout = TimeSpan.FromSeconds(validation.Options.StartupTimeoutSeconds);
    }

    /// <summary>Creates a service-level physical warm instance without a room, grant, or allocation.</summary>
    public AfdevProcessHost(AfdevRuntimeValidation validation, int slot, Action<string>? log = null)
        : this(validation, Guid.Empty, 0, slot, log) { }

    /// <summary>Physical identity exclusive to this host and independent of per-round allocations.</summary>
    public Guid InstanceId { get; }

    /// <summary>
    /// Immutable binding between the first world and a real round. The host cannot be leased again after
    /// stopping.
    /// </summary>
    public AfdevWorldBinding? Binding { get; private set; }

    /// <summary>
    /// Prepares only resources and the engine, deferring first OPEN to a real binding so idle time cannot
    /// consume a match.
    /// </summary>
    public async Task WarmAsync(CancellationToken cancellationToken = default)
        => await StartCoreAsync(true, cancellationToken).ConfigureAwait(false);

    /// <summary>Checks an unbound physical engine; this state is not SESSION_READY.</summary>
    public async Task CheckWarmAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (State != AfdevRuntimeState.EngineReady || Binding != null || bootstrap == null)
                throw new InvalidOperationException("物理实例没有可绑定的预热引擎。");

            bootstrap.VerifyEngineReady();
        }
        finally { operationGate.Release(); }
    }

    /// <summary>
    /// Consumes frozen capacity and slots, returning once the real world and preparation coordination gate
    /// are ready. This round's background task continues bot-resource preparation.
    /// </summary>
    public Task<AfdevNativeEvidence> BindAsync(MatchStartSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        AfdevMatchStartValidator.Validate(snapshot);
        snapshot.BioBotPlan?.Validate(snapshot);

        return BindCoreAsync(snapshot.AllocationId, snapshot.RoomId, snapshot.Descriptor.ModeId,
            snapshot.Descriptor.MapId, snapshot.FighterCapacity, snapshot.BioBotPlan, cancellationToken, snapshot.Descriptor);
    }

    /// <summary>
    /// Retains four-player capacity for diagnostics without a roster. Production starts use the
    /// full-snapshot overload.
    /// </summary>
    public Task<AfdevNativeEvidence> BindAsync(Guid newAllocationId, ulong newRoomId,
        CancellationToken cancellationToken = default)
        => BindCoreAsync(newAllocationId, newRoomId, validation.ModeId, validation.MapId, 4, null, cancellationToken);

    /// <summary>
    /// Retains the direct diagnostic-plan entry point, taking capacity from the plan. Production starts
    /// additionally validate the full human snapshot.
    /// </summary>
    public Task<AfdevNativeEvidence> BindAsync(Guid newAllocationId, ulong newRoomId,
        AfdevBioBotPlan? botPlan, CancellationToken cancellationToken = default)
        => BindCoreAsync(newAllocationId, newRoomId, validation.ModeId, validation.MapId,
            botPlan?.FighterCapacity ?? 4, botPlan, cancellationToken);

    private async Task<AfdevNativeEvidence> BindCoreAsync(Guid newAllocationId, ulong newRoomId,
        uint modeId, ushort mapId, byte fighterCapacity, AfdevBioBotPlan? botPlan, CancellationToken cancellationToken,
        NativeMatchDescriptor? descriptor = null)
    {
        if (newAllocationId == Guid.Empty || newRoomId == 0)
            throw new ArgumentException("绑定必须使用真实 allocation 与房间。");

        AfdevMatchStartValidator.ValidateCapacity(modeId, mapId, fighterCapacity);
        botPlan?.Validate();

        if (botPlan is not null && (!AfdevMapCatalog.SupportsBioBots(modeId, mapId) ||
            botPlan.AllocationId != newAllocationId || botPlan.RoomId != newRoomId ||
            botPlan.FighterCapacity != fighterCapacity))
            throw new InvalidOperationException("Bot 计划的 allocation、房间和容量必须与本次首次 OPEN 一致。");

        bool bindingStarted = false;

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (State != AfdevRuntimeState.EngineReady || Binding != null || allocationId != Guid.Empty ||
                bootstrap == null || probe == null)
                throw new InvalidOperationException("预热实例已经被消费或不可用。");

            using var bindingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                stopRequested.Token, botWorkStopping.Token);

            bindingCancellation.CancelAfter(startupTimeout);
            bindingCancellation.Token.ThrowIfCancellationRequested();

            AfdevRuntimeValidation selected = validation.SelectRoute(modeId, mapId);

            bindingStarted = true;
            allocationId = newAllocationId;
            roomId = newRoomId;
            boundValidation = selected;
            Binding = new(InstanceId, attemptId, 1, allocationId, roomId) { ModeId = modeId, MapId = mapId };
            bootstrap.SelectRoute(modeId, mapId);
            Report($"AFDEV_BIND_BUDGET world_seconds={startupTimeout.TotalSeconds:F0} bots={botPlan?.Bots.Length ?? 0}; Bot 准备使用独立期限。");
            Transition(AfdevRuntimeState.Initializing, $"真实回合已独占绑定；首次加载 {selected.Target.MapName}。");
            await bootstrap.InitializeAsync(fighterCapacity, bindingCancellation.Token).ConfigureAwait(false);
            Transition(AfdevRuntimeState.WaitingForReadiness, probe.WaitingCondition);

            var evidence = BindEvidence(await probe.WaitAsync(bindingCancellation.Token).ConfigureAwait(false));

            bindingCancellation.Token.ThrowIfCancellationRequested();
            await bootstrap.ApplyPveSettingsAsync(evidence, descriptor?.SubModeId ?? 0,
                descriptor?.RoomFlags ?? 0, bindingCancellation.Token).ConfigureAwait(false);
            if (AfdevMapCatalog.SupportsPvp(modeId, mapId))
                await bootstrap.ApplyPvpAsync(evidence, descriptor ??
                    throw new InvalidOperationException("PvP 必须提供冻结房间设置和席位。"), bindingCancellation.Token, botRuntime).ConfigureAwait(false);
            RecordWorldReady(botPlan?.Bots.Length ?? 0);
            await BeginBoundBotPreparationAsync(botPlan, evidence, bindingCancellation.Token).ConfigureAwait(false);
            bindingCancellation.Token.ThrowIfCancellationRequested();
            PublishReady(evidence);
            StartBoundBotWork(botPlan, evidence);

            return evidence;
        }
        catch
        {
            if (bindingStarted)
            {
                ReadyEvidence = null;
                State = AfdevRuntimeState.Failed;
            }

            throw;
        }
        finally { operationGate.Release(); }
    }

    private AfdevNativeEvidence BindEvidence(AfdevNativeEvidence evidence)
    {
        if (evidence.InstanceId != InstanceId || evidence.AttemptId != attemptId || evidence.WorldGeneration != 1)
            throw new InvalidOperationException("原生证据不属于当前物理实例和首次世界。");
        if (Binding is not { } binding || binding.InstanceId != InstanceId || binding.AttemptId != attemptId)
            throw new InvalidOperationException("原生世界尚未绑定真实回合。");
        if (evidence.ModeId != binding.ModeId || evidence.MapId != binding.MapId ||
            evidence.MapName != CurrentValidation.Target.MapName || evidence.GameClass != CurrentValidation.Target.GameClass)
            throw new InvalidOperationException("原生地图证据与本局冻结路由不一致，不能继承预热默认地图或其他回合证据。");

        if (ReadyEvidence is { } previous && (previous.World != evidence.World ||
            previous.GameInfo != evidence.GameInfo || previous.NetDriver != evidence.NetDriver))
            throw new InvalidOperationException("原生世界已改变；未经验证的新世界不能继承本回合绑定。");

        return evidence with { AllocationId = binding.AllocationId };
    }

    /// <summary>Current host stage. SESSION_READY can arise only from this attempt's native evidence.</summary>
    public AfdevRuntimeState State { get; private set; } = AfdevRuntimeState.Created;

    /// <summary>
    /// Latest complete native evidence, cleared when shutdown begins or evidence becomes invalid.
    /// </summary>
    public AfdevNativeEvidence? ReadyEvidence { get; private set; }

    /// <summary>
    /// Current startup-attempt private directory for diagnostics, never for recovering process ownership.
    /// </summary>
    public string? AttemptDirectory => workspace?.Directory;

    /// <summary>Actual attempts, including the first.</summary>
    public int AttemptCount => attemptNumber;

    /// <summary>
    /// A single control-plane health check. On failure, the eligibility owner revokes grants and stops
    /// relays before explicitly stopping this host.
    /// </summary>
    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ReadyEvidence = await CheckBoundReadyAsync(cancellationToken).ConfigureAwait(false);
            CheckBotWork();
            if (reviveCoinWork?.IsCompleted == true)
                throw new IOException("本局 PVE 复活币事务工作已停止，不能继续发布就绪。");
        }
        catch
        {
            ReadyEvidence = null;

            throw;
        }
        finally { operationGate.Release(); }
    }

    private async Task<AfdevNativeEvidence> CheckBoundReadyAsync(CancellationToken cancellationToken)
    {
        if (State != AfdevRuntimeState.SessionReady || probe is null || ReadyEvidence is not { } expected ||
            Binding is not { } binding || expected.AllocationId != binding.AllocationId ||
            expected.WorldGeneration != binding.WorldGeneration)
            throw new InvalidOperationException("AFDEV 当前没有可核验的严格绑定实例。");

        // Retain prior evidence until final BindEvidence. Array relocation is not world reset and cannot justify a new world or identity.
        _ = BindEvidence(expected);

        using var checking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopRequested.Token);
        var evidence = await probe.CheckAsync(expected, checking.Token).ConfigureAwait(false);

        checking.Token.ThrowIfCancellationRequested();
        if (evidence is not null) return BindEvidence(evidence);
        throw new IOException($"AFDEV 就绪条件已失效：{probe.WaitingCondition}");
    }

    /// <summary>
    /// Progresses from isolated-directory preparation to strict native readiness. Fully reclaim the old
    /// tree, handles, ports, and state after each failure before retrying.
    /// </summary>
    public async Task<AfdevNativeEvidence> StartAsync(CancellationToken cancellationToken = default)
        => await StartCoreAsync(false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("原生世界没有返回就绪证据。");

    private async Task<AfdevNativeEvidence?> StartCoreAsync(bool warmOnly, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (startAccepted || State != AfdevRuntimeState.Created)
                throw new InvalidOperationException("此 AFDEV 宿主已经接受启动；重复启动不能复用身份或就绪证据。");
            if (!warmOnly && AfdevMapCatalog.SupportsPvp(validation.ModeId, validation.MapId))
                throw new InvalidOperationException("PvP 须通过 BindAsync 消费冻结房间设置和真人席位。");
            if (!warmOnly && botBridgeEnabled && AfdevMapCatalog.SupportsBioBots(validation.ModeId, validation.MapId))
                throw new InvalidOperationException("Bot 实验需要预热实例与冻结回合计划，通过 BindAsync 消费，不能直接启动空房 Bot。");

            startAccepted = true;

            using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopRequested.Token);

            try
            {
                starting.Token.ThrowIfCancellationRequested();
                reservation = AfdevRuntimeReservation.Acquire(validation.Options, InstanceId, slot);

                var allocationDirectory = RuntimePath.ResolveUnderRoot(validation.Options.InstancesRoot, InstanceId.ToString("N"));

                if (warmOnly && (allocationId != Guid.Empty || roomId != 0))
                    throw new InvalidOperationException("服务级预热不能提前关联房间。");

                if (Directory.Exists(allocationDirectory) || File.Exists(allocationDirectory))
                    throw new IOException("AFDEV 物理实例目录已存在；拒绝旧 ready、PID 和未知所有权。");

                for (attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
                {
                    attemptId = Guid.NewGuid();
                    processId = 0;
                    processCreatedUtc = null;
                    workspace = null;
                    attemptCleaned = false;
                    State = AfdevRuntimeState.Preparing;

                    try
                    {
                        starting.Token.ThrowIfCancellationRequested();
                        workspace = await AfdevInstanceWorkspace.PreparePhysicalAttemptAsync(validation, InstanceId,
                            slot, attemptNumber, attemptId, starting.Token).ConfigureAwait(false);
                        manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(
                            File.ReadAllBytes(Path.Combine(workspace.Directory, "runtime-manifest.json"))));
                        Transition(AfdevRuntimeState.Prepared,
                            workspace.UsesReservedRuntime
                                ? $"预留客户端已原位核验：{workspace.RuntimeRoot}；未复制客户端，独立诊断目录准备 {workspace.PreparationMilliseconds:F0} ms。"
                                : $"独立目录及 manifest 已校验：私有复制 {workspace.PrivateResourceBytes} 字节，共享静态包 {workspace.SharedResourceBytes} 字节，准备 {workspace.PreparationMilliseconds:F0} ms。");

                        // The timeout covers native startup only. Shared-package checks and private-directory preparation remain subject to external cancellation.
                        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(starting.Token);

                        attemptCancellation.CancelAfter(startupTimeout);
                        process = AfdevOwnedProcess.CreateSuspended(workspace,
                            $"-log -windowed -port={workspace.TargetPort} ResX=640 ResY=480");
                        processId = process.ProcessId;
                        processCreatedUtc = process.CreationTimeUtc;
                        bootstrap = new AfdevNativeBootstrap(process, workspace, validation, attemptId, ReportNative);
                        probe = new AfdevReadinessProbe(bootstrap, reservation);
                        Transition(AfdevRuntimeState.Suspended, "CreateProcessW 已挂起创建并加入 kill-on-close Job。");
                        InstallBotBridgeSuspended();
                        bootstrap.PrepareSuspended();
                        attemptCancellation.Token.ThrowIfCancellationRequested();
                        reservation.ReleaseTargetForNative(process);
                        process.ResumeMainThread();
                        Transition(AfdevRuntimeState.Initializing, "主线程已恢复，等待引擎与本次 OPEN。");
                        await bootstrap.PrepareEngineAsync(attemptCancellation.Token).ConfigureAwait(false);

                        if (warmOnly)
                        {
                            Transition(AfdevRuntimeState.EngineReady, "引擎预热完成；尚无 BIO 世界或玩家资格，等待真实开赛绑定。");

                            return null;
                        }

                        if (allocationId == Guid.Empty || roomId == 0)
                            throw new InvalidOperationException("完整启动必须提供真实回合身份。");

                        Binding = new(InstanceId, attemptId, 1, allocationId, roomId)
                        {
                            ModeId = validation.ModeId,
                            MapId = validation.MapId
                        };
                        await bootstrap.InitializeAsync(attemptCancellation.Token).ConfigureAwait(false);
                        Transition(AfdevRuntimeState.WaitingForReadiness, probe.WaitingCondition);

                        var evidence = BindEvidence(await probe.WaitAsync(attemptCancellation.Token).ConfigureAwait(false));

                        attemptCancellation.Token.ThrowIfCancellationRequested();
                        await bootstrap.ApplyPveSettingsAsync(evidence, 0, 0,
                            attemptCancellation.Token).ConfigureAwait(false);
                        PublishReady(evidence);

                        return evidence;
                    }
                    catch (Exception failure)
                    {
                        if (failure is AfdevProcessCreationException creationFailure)
                        {
                            process = creationFailure.OwnedProcess;
                            processId = process.ProcessId;
                            processCreatedUtc = process.CreationTimeUtc;
                        }
                        if (failure is AfdevWorkspacePreparationException preparationFailure)
                            pendingPreparation = preparationFailure;

                        var reason = failure is OperationCanceledException && !starting.IsCancellationRequested
                            ? $"原生启动超时；首个未满足条件：{probe?.WaitingCondition ?? "引擎初始化"}。"
                            : $"{failure.GetType().Name}: {failure.Message}；阶段：{probe?.WaitingCondition ?? State.ToString()}";

                        bootstrap?.CaptureReadinessDiagnostic("attempt-ending", force: true);
                        RecordFailure(reason);
                        await CleanupAttemptAsync().ConfigureAwait(false);

                        if (starting.IsCancellationRequested)
                            throw new OperationCanceledException("AFDEV 启动已取消并完成本次清理。", failure, starting.Token);
                        if (attemptNumber == maxAttempts)
                            throw new InvalidOperationException($"AFDEV {attemptNumber} 次启动均未通过：{reason}", failure);

                        Report($"旧进程树、句柄、端口及瞬态状态均已回收；准备第 {attemptNumber + 1}/{maxAttempts} 次尝试。");
                    }
                }

                throw new InvalidOperationException("AFDEV 尝试次数状态无效。");
            }
            catch
            {
                if (State != AfdevRuntimeState.CleanupPending)
                {
                    try
                    {
                        await CleanupAttemptAsync().ConfigureAwait(false);
                        reservation?.ReleaseAfterProcessStopped();
                        State = AfdevRuntimeState.Failed;
                    }
                    catch
                    {
                        State = AfdevRuntimeState.CleanupPending;

                        throw;
                    }
                }

                throw;
            }
        }
        finally { operationGate.Release(); }
    }

    /// <summary>
    /// Continuously checks the same native world, socket, v4 patch, and ownership after readiness. A zero
    /// duration waits for cancellation; invalidation immediately revokes readiness and cleans up.
    /// </summary>
    public async Task ObserveAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        using var observing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopRequested.Token);
        var started = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (duration == TimeSpan.Zero || started.Elapsed < duration)
            {
                observing.Token.ThrowIfCancellationRequested();
                await operationGate.WaitAsync(observing.Token).ConfigureAwait(false);

                try
                {
                    var evidence = await CheckBoundReadyAsync(observing.Token).ConfigureAwait(false);

                    CheckBotWork();

                    WriteAtomic("ready.json", Snapshot("SESSION_READY", "同一启动尝试的原生条件已重新核实。", evidence));
                    ReadyEvidence = evidence;
                }
                finally { operationGate.Release(); }

                await Task.Delay(250, observing.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Reads the actual native player for the current relay, serialized with initialization and shutdown.
    /// Calls no engine functions and grants no new player rights.
    /// </summary>
    public async Task<AfdevNativePlayerEvidence?> ReadPlayerEvidenceAsync(System.Net.IPEndPoint upstream,
        ulong uin, string nickname, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (State != AfdevRuntimeState.SessionReady || ReadyEvidence == null || bootstrap == null)
                throw new InvalidOperationException("只能读取当前严格就绪实例的玩家证据。");

            cancellationToken.ThrowIfCancellationRequested();

            var evidence = bootstrap.ReadPlayerEvidence(upstream, uin, nickname);

            return evidence is null ? null : evidence with { AllocationId = allocationId };
        }
        finally { operationGate.Release(); }
    }

    /// <summary>
    /// Cancels startup/supervision and serially reclaims the process tree. Failed cleanup retains
    /// CleanupPending and reservations for retry.
    /// </summary>
    public async Task StopAsync()
    {
        stopRequested.Cancel();
        botWorkStopping.Cancel();
        await operationGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (State == AfdevRuntimeState.Stopped)
                return;

            await CleanupAttemptAsync().ConfigureAwait(false);

            try
            {
                reservation?.ReleaseAfterProcessStopped();
                State = AfdevRuntimeState.Stopped;
                Report("STOPPED：拥有的进程树已退出，句柄、端口、槽和瞬态状态已回收。");
            }
            catch
            {
                State = AfdevRuntimeState.CleanupPending;

                throw;
            }
        }
        finally { operationGate.Release(); }
    }

    /// <summary>
    /// Equivalent to one explicit stop. On failure, the object still owns reservations and must not be
    /// discarded.
    /// </summary>
    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task CleanupAttemptAsync()
    {
        if (botWorkTask != null)
            botWorkStopping.Cancel();
        ReadyEvidence = null;
        State = AfdevRuntimeState.Stopping;

        if (attemptCleaned) return;

        try
        {
            // Revoke published files before termination. A deletion failure must not prevent attempting owned-tree termination.
            Exception? stateFailure = null;

            try { ClearTransientState(); }
            catch (Exception failure) { stateFailure = failure; }

            // Worker tasks depend only on Runtime, never acquiring the host operation gate. Wait for them before closing process handles or diagnostics.
            try { await WaitForBotWorkAsync().ConfigureAwait(false); }
            catch (Exception failure) { Report($"BIO_BOT_WORK_STOPPED_WITH_FAILURE kind={failure.GetType().Name}"); }

            await StopReviveCoinsAsync().ConfigureAwait(false);

            if (process != null)
                await process.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            if (reservation is { IsReleased: false })
                reservation.ReclaimTargetAfterProcessStopped();

            pendingPreparation?.Cleanup();
            pendingPreparation = null;

            if (stateFailure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stateFailure).Throw();

            AppendJournal("ATTEMPT_CLEANED", "整个 Job 零活动进程；句柄关闭；上游端口重新独占绑定；ready/state/pid 已清除。");
            workspace?.ArchiveAndRemoveStoppedResources();
            attemptCleaned = true;
            process = null;
            bootstrap = null;
            probe = null;
            botRuntime = null;
            botWorkTask = null;
            botPreparationTask = null;
            Volatile.Write(ref botPreparations, []);
        }
        catch
        {
            State = AfdevRuntimeState.CleanupPending;

            throw;
        }
    }

    private void PublishReady(AfdevNativeEvidence evidence)
    {
        bootstrap!.VerifyNoNativeFailure();

        if (process == null || !process.IsAlive || reservation == null || !reservation.VerifyTargetOwner(process))
            throw new InvalidOperationException("发布前精确进程或 UDP 所有权已失效。");

        WriteAtomic("ready.json", Snapshot("SESSION_READY", "严格原生检查全部满足。", evidence));
        ReadyEvidence = BindEvidence(evidence);
        Transition(AfdevRuntimeState.SessionReady, "SESSION_READY：本次原生 v4、目标地图、GameInfo、live socket zero-key 和端口归属均已核实。");
    }

    private void Transition(AfdevRuntimeState state, string message)
    {
        State = state;
        WriteAtomic("state.json", Snapshot(state.ToString(), message));
        AppendJournal(state.ToString(), message);
        Report(message);
    }

    private void RecordFailure(string message)
    {
        ReadyEvidence = null;
        State = AfdevRuntimeState.Failed;

        // Diagnostic-write failure cannot block process cleanup. ClearTransientState still strictly verifies state revocation afterward.
        try { AppendJournal("FAILED", message); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Report(message);
    }

    private AfdevRuntimeValidation CurrentValidation => boundValidation ?? validation;

    private object Snapshot(string state, string message, AfdevNativeEvidence? evidence = null) => new SnapshotJson1
    {
        readiness_contract = "afdev-round3-v1-loopback-remote-query",
        instance_id = InstanceId, world_binding = Binding, state = state, message = message, observed_utc = DateTimeOffset.UtcNow, allocation_id = allocationId,
        room_id = roomId, attempt_number = attemptNumber, attempt_id = attemptId,
        process_id = processId, process_created_utc = processCreatedUtc,
        executable = workspace?.ExecutablePath, working_directory = workspace?.WorkingDirectory,
        runtime_root = workspace?.RuntimeRoot, reserved_runtime = workspace?.UsesReservedRuntime,
        public_port = workspace?.PublicPort, target_port = workspace?.TargetPort,
        mode_id = CurrentValidation.ModeId, map_id = CurrentValidation.MapId,
        map = CurrentValidation.Target.MapName, game_info = CurrentValidation.Target.GameClass,
        manifest_sha256 = manifestSha256, runtime_version = validation.Manifest.RuntimeVersion,
        afdev_sha256 = validation.Manifest.AfdevSha256,
        servermove_v4_sha256 = validation.Manifest.ServerMoveV4Sha256,
        mode_map_catalog_version = validation.Manifest.ModeMapCatalogVersion, native = evidence
    };

    private void ClearTransientState()
    {
        if (workspace == null)
            return;

        foreach (var name in new[] { "ready.json", "state.json", "afdev.pid" })
        {
            var path = RuntimePath.ResolveUnderRoot(workspace.Directory, name);

            File.Delete(path);

            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"AFDEV 瞬态状态未能清除：{path}");
        }
    }

    private void WriteAtomic(string name, object value)
    {
        if (workspace == null)
            throw new InvalidOperationException("AFDEV 状态尚无本次尝试目录。");

        var destination = RuntimePath.ResolveUnderRoot(workspace.Directory, name);
        var temporary = RuntimePath.ResolveUnderRoot(workspace.Directory, $".{name}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporary, global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(value, JsonOptions));
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
    }

    private void AppendJournal(string state, string message)
    {
        if (workspace == null)
            return;

        var path = RuntimePath.ResolveUnderRoot(workspace.Directory, "logs/lifecycle.jsonl");
        var compactOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = false };

        // World supervision and bot preparation may log concurrently for the same instance. Serialize writes so log-sharing conflicts cannot fail preparation.
        lock (journalGate)
            File.AppendAllText(path, global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(Snapshot(state, message, ReadyEvidence), compactOptions) + Environment.NewLine);
    }

    private void ReportNative(string message)
    {
        AppendJournal("NATIVE", message);
        Report(message);
    }

    private void Report(string message)
    {
        // Display subscribers do not own processes; UI exceptions must not block resource cleanup.
        try { log?.Invoke($"[{InstanceId:N}/{attemptNumber}/{attemptId:N}] allocation={allocationId:N} {message}"); }
        catch (Exception) { }
    }
}

/// <summary>
/// AFDEV control-plane stages. Only SessionReady means strict native checks passed; CleanupPending must
/// retain reservations.
/// </summary>
public enum AfdevRuntimeState
{
    Created, Preparing, Prepared, Suspended, EngineReady, Initializing, WaitingForReadiness,
    SessionReady, Stopping, Stopped, Failed, CleanupPending
}

/// <summary>
/// One-time binding of a physical process's first world to a real round. Does not prove same-PID reset
/// support.
/// </summary>
public sealed record AfdevWorldBinding(Guid InstanceId, Guid AttemptId, long WorldGeneration,
    Guid AllocationId, ulong RoomId)
{
    /// <summary>Mode ID frozen at first OPEN, never overridden by the default warmup route.</summary>
    public uint ModeId { get; init; }

    /// <summary>Map ID frozen at first OPEN and required to remain unchanged by later health checks.</summary>
    public ushort MapId { get; init; }
}
