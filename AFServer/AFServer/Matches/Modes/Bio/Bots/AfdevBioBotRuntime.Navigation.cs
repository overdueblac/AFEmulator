namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private uint navigationCommand;
    private uint motionCommand;
    private uint reachCommand;
    private uint navigationStopCommand;
    private AfdevBioBotNavigation? navigation;
    private AfdevBioBotReachQuery? reachQuery;
    private AfdevBioBotRouteRun? routeRun;
    private AfdevBioBotPhysicsObservers? physicsObservers;
    private int patrolStarted;
    private readonly object navigationGate = new();

    /// <summary>
    /// Registers a dedicated state-command page during installation. The same process owns page/script; the
    /// binding cannot reset.
    /// </summary>
    internal void ConfigureNavigation(uint command, uint motion, uint reach, uint stop)
    {
        if (command < 0x10000 || motion < 0x10000 || reach < 0x10000 || stop < 0x10000 ||
            navigationCommand != 0 || motionCommand != 0 || reachCommand != 0)
            throw new InvalidOperationException("导航命令页未分配或已绑定。");

        navigationCommand = command;
        motionCommand = motion;
        reachCommand = reach;
        navigationStopCommand = stop;
    }

    /// <summary>
    /// Runs this slot's sole continuous patrol. The host retains and awaits the task set; cancellation
    /// stops new action publication.
    /// </summary>
    public async Task RunContinuousNavigationAsync(AfdevBioBotPreparation participant,
        CancellationToken cancellationToken = default)
    {
        _ = GetNavigation(participant);

        if (Interlocked.CompareExchange(ref patrolStarted, 1, 0) != 0)
            throw new InvalidOperationException("同一 BIO Bot 槽只能有一个持续导航调度任务。");

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);
        AfdevBioBotWalkRoutes learned = await GetWalkRoutesAsync(running.Token).ConfigureAwait(false);
        var patrol = new AfdevBioBotPatrol(this, process, world!, participant, Save, CheckBinding, learnedRoutes: learned);

        await patrol.RunAsync(running.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Queries native arrival for the current Pawn at a hop or final target. Exceptions/cancellation
    /// produce no arrival result.
    /// </summary>
    public Task<AfdevBioBotReachResult> QueryNativeReachAsync(AfdevBioBotPreparation participant, uint goal,
        CancellationToken cancellationToken = default, uint expectedPawn = 0) =>
        QueryNativeReachCoreAsync(participant, goal, null, cancellationToken, expectedPawn);

    /// <summary>
    /// Queries native arrival at frozen coordinates with NewAnchor=None, using the same query slot and
    /// lifetime boundary.
    /// </summary>
    public Task<AfdevBioBotReachResult> QueryNativeReachAsync(AfdevBioBotPreparation participant, AfdevBioBotPoint point,
        CancellationToken cancellationToken = default, uint expectedPawn = 0) =>
        QueryNativeReachCoreAsync(participant, 0, point, cancellationToken, expectedPawn);

    private async Task<AfdevBioBotReachResult> QueryNativeReachCoreAsync(AfdevBioBotPreparation participant, uint goal,
        AfdevBioBotPoint? point,
        CancellationToken cancellationToken = default, uint expectedPawn = 0)
    {
        using var querying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);
        AfdevBioBotReachQuery query;

        lock (navigationGate)
        {
            _ = GetNavigation(participant);
            query = reachQuery ??= new AfdevBioBotReachQuery(process, world!, participant, onlineBotId,
                onlineIsolationPage, reachCommand, Save, CheckBinding);
        }

        return await query.ExecuteAsync(goal, querying.Token, expectedPawn, point).ConfigureAwait(false);
    }

    /// <summary>
    /// Serially executes a bounded native route, checking arrival and retiring each segment before
    /// replanning. The continuous behavior loop exclusively owns publication.
    /// </summary>
    public Task<AfdevBioBotRouteResult> RunNavigationRouteAsync(AfdevBioBotPreparation participant, uint goal,
        CancellationToken cancellationToken = default, AfdevBioBotNavigationMode mode = AfdevBioBotNavigationMode.FindPath) =>
        RunNavigationRouteCoreAsync(participant, goal, null, cancellationToken, mode);

    /// <summary>
    /// Reuses route ownership for PointReachable followed by MoveTo. queryOnly neither moves nor
    /// establishes arrival.
    /// </summary>
    public Task<AfdevBioBotRouteResult> RunNavigationRouteAsync(AfdevBioBotPreparation participant, AfdevBioBotPoint point,
        CancellationToken cancellationToken = default, bool queryOnly = false) =>
        RunNavigationRouteCoreAsync(participant, 0, point, cancellationToken,
            queryOnly ? AfdevBioBotNavigationMode.PointReachability : AfdevBioBotNavigationMode.Point);

    private async Task<AfdevBioBotRouteResult> RunNavigationRouteCoreAsync(AfdevBioBotPreparation participant, uint goal,
        AfdevBioBotPoint? point,
        CancellationToken cancellationToken = default, AfdevBioBotNavigationMode mode = AfdevBioBotNavigationMode.FindPath,
        AfdevBioBotWalkStep? walkStep = null)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);
        AfdevBioBotRouteRun route;

        lock (navigationGate)
        {
            _ = GetNavigation(participant);
            route = routeRun ??= new AfdevBioBotRouteRun(this, participant, Save);
        }

        return await route.ExecuteAsync(goal, running.Token, mode, point, walkStep).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits fixed navigation or directly reachable programs, one action at a time. Script-slot reuse
    /// requires completed retirement.
    /// </summary>
    public Task<AfdevBioBotNavigationState> StartNavigationAsync(AfdevBioBotPreparation participant, uint goal,
        bool diagnostic = false, CancellationToken cancellationToken = default,
        AfdevBioBotNavigationMode mode = AfdevBioBotNavigationMode.FindPath) =>
        StartNavigationCoreAsync(participant, goal, null, diagnostic, cancellationToken, mode);

    /// <summary>
    /// Submits a frozen-coordinate program. Normal movement should use RunNavigationRouteAsync for prior
    /// reachability checks.
    /// </summary>
    public Task<AfdevBioBotNavigationState> StartNavigationAsync(AfdevBioBotPreparation participant, AfdevBioBotPoint point,
        bool diagnostic = false, CancellationToken cancellationToken = default, bool queryOnly = false) =>
        StartNavigationCoreAsync(participant, 0, point, diagnostic, cancellationToken,
            queryOnly ? AfdevBioBotNavigationMode.PointReachability : AfdevBioBotNavigationMode.Point);

    private async Task<AfdevBioBotNavigationState> StartNavigationCoreAsync(AfdevBioBotPreparation participant, uint goal,
        AfdevBioBotPoint? point,
        bool diagnostic = false, CancellationToken cancellationToken = default,
        AfdevBioBotNavigationMode mode = AfdevBioBotNavigationMode.FindPath)
    {
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);

        return await GetNavigation(participant).StartAsync(goal, diagnostic, starting.Token, mode, point).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads native progress for the exact task/Pawn generation. Position changes, latent state, and Stop
    /// do not establish arrival.
    /// </summary>
    public Task<AfdevBioBotNavigationState> CheckNavigationAsync(AfdevBioBotPreparation participant,
        CancellationToken cancellationToken = default) => GetNavigation(participant).CheckAsync(cancellationToken);

    /// <summary>
    /// Enables native physics for the running same-generation MoveTo/MoveToward, preserving position,
    /// velocity, and server timestamp.
    /// </summary>
    public async Task StartNativeMotionAsync(AfdevBioBotPreparation participant,
        CancellationToken cancellationToken = default, bool observeOnly = false)
    {
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);

        await GetNavigation(participant).StartMotionAsync(starting.Token, observeOnly).ConfigureAwait(false);
    }

    /// <summary>
    /// Enables native physics after an observation-only baseline, retaining original counters for
    /// comparison.
    /// </summary>
    public async Task EnableNativePhysicsAsync(AfdevBioBotPreparation participant,
        CancellationToken cancellationToken = default)
    {
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);

        await GetNavigation(participant).EnablePhysicsAsync(starting.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops physics publication, then lets the original controller retire owned latent work. Unknown
    /// ownership/completion requires original-Job cleanup.
    /// </summary>
    public Task<AfdevBioBotNavigationState> StopNavigationAsync(AfdevBioBotPreparation participant,
        CancellationToken cancellationToken = default) => GetNavigation(participant, stopping: true).StopAsync(cancellationToken);

    private AfdevBioBotNavigation GetNavigation(AfdevBioBotPreparation participant, bool stopping = false)
    {
        ArgumentNullException.ThrowIfNull(participant);

        lock (navigationGate)
        {
            AfdevNativeEvidence bound = world ?? throw new InvalidOperationException("Bot 尚未绑定原生世界。");

            if (navigationCommand == 0 || preparedBot == null || preparedBot != participant ||
                participant.InstanceId != bound.InstanceId || participant.AttemptId != bound.AttemptId ||
                participant.AllocationId != bound.AllocationId || participant.WorldGeneration != bound.WorldGeneration ||
                participant.ProcessId != bound.ProcessId || participant.ProcessCreationTimeUtc != bound.ProcessCreationTimeUtc ||
                participant.World != bound.World || participant.GameInfo != bound.GameInfo)
                throw new InvalidDataException("导航调用不属于已经准备的同一原生参赛者。");

            if (!stopping)
            {
                workStopping.Token.ThrowIfCancellationRequested();
                CheckBinding();

                var reader = new QueryInspector(process, cacheMetadata: true);

                if (reader.U32(factoryCommand + 8) != 1 || reader.U32(factoryCommand + 12) != 1 ||
                    reader.U32(factoryCommand + 0x30) != participant.Controller ||
                    reader.U32(reader.FieldAddress(participant.Controller, "PlayerReplicationInfo", "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4)) != participant.Pri)
                    throw new InvalidDataException("导航参赛者已经失去原生工厂或 PRI 绑定。");
            }

            // Shutdown needs only immutable preparation identity; cleanup results must remain queryable after world changes or process exit.
            return navigation ??= new AfdevBioBotNavigation(process, bound, participant.Controller, navigationCommand, Save,
                new AfdevBioBotMotionRun(process, bound, motionCommand, trace, factoryCommand, Save,
                    physicsObservers ?? throw new InvalidOperationException("Bot 物理命令尚未加入本进程共享入口。")),
                new AfdevBioBotNavigationStop(process, bound, participant, onlineBotId, onlineIsolationPage,
                    navigationStopCommand, Save));
        }
    }
}
