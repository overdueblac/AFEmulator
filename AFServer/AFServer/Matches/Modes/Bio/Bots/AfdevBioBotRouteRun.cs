using System.Diagnostics;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Native route for one BIO participant, limited to six segments and two stall/deadline recoveries.
/// Continuous behavior selects later targets. Retire each segment before reuse; stop cancels first, then
/// awaits executor exit.
/// </summary>
internal sealed partial class AfdevBioBotRouteRun(AfdevBioBotRuntime runtime, AfdevBioBotPreparation participant,
    Action<string, object> save)
{
    private const int MaximumSegments = 6;
    private const int MaximumRecoveries = 2;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool failed;

    public async Task WaitForIdleAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        gate.Release();
    }

    public async Task<AfdevBioBotRouteResult> ExecuteAsync(uint goal, CancellationToken token,
        AfdevBioBotNavigationMode mode = AfdevBioBotNavigationMode.FindPath, AfdevBioBotPoint? point = null,
        AfdevBioBotWalkStep? walkStep = null)
    {
        Guid routeId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var steps = new List<AfdevBioBotRouteStep>();
        AfdevBioBotNavigationState? state = null;
        AfdevBioBotReachResult? hopReach = null;
        AfdevBioBotReachResult? finalReach = null;
        uint hop = 0;
        int segments = 0;
        int recoveries = 0;
        bool entered = false;
        bool ownsProgram = false;
        bool uncertainCommand = false;
        bool pointChecked = false;
        uint pointPawn = 0;
        long pointPawnGeneration = 0;
        bool observedMove = false;
        Stopwatch? observedElapsed = null;

        AfdevBioBotRouteResult Finish(AfdevBioBotRouteOutcome outcome, string reason, bool requiresProcessStop = false)
        {
            if (requiresProcessStop)
                failed = true;

            var result = new AfdevBioBotRouteResult(routeId, outcome, goal, segments, recoveries,
                requiresProcessStop, reason, steps.ToArray(), startedAt, DateTimeOffset.UtcNow)
                { Mode = mode, Point = point, UsedObservedStep = observedMove };

            save("route-result", result);
            return result;
        }

        void Record(AfdevBioBotNavigationState retired)
        {
            if (state == null || state.TaskId == Guid.Empty || steps.Any(item => item.TaskId == state.TaskId))
                return;

            var step = new AfdevBioBotRouteStep(segments, state.TaskId, state.PawnGeneration, state.Pawn, hop,
                state.Phase, hopReach, finalReach, retired);

            steps.Add(step);
            save("route-step-" + segments, step);
        }

        async Task<AfdevBioBotNavigationState> RetireAsync()
        {
            // Canceling user work must still retire published programs. The native bridge's own wait remains bounded.
            AfdevBioBotNavigationState retired = await runtime.StopNavigationAsync(participant, CancellationToken.None).ConfigureAwait(false);

            ownsProgram = false;
            Record(retired);
            return retired;
        }

        async Task<(bool Failed, string Reason)> CleanupAsync()
        {
            if (!ownsProgram && !uncertainCommand)
                return (false, string.Empty);

            try
            {
                AfdevBioBotNavigationState retired = await RetireAsync().ConfigureAwait(false);

                return (retired.RequiresProcessStop || retired.Active, retired.Reason);
            }
            catch (Exception error)
            {
                return (true, error.Message);
            }
        }

        async Task<AfdevBioBotReachResult> ReachAsync(uint target, AfdevBioBotPoint? destination = null)
        {
            uncertainCommand = true;

            AfdevBioBotReachResult result = destination is { } position
                ? await runtime.QueryNativeReachAsync(participant, position, token, expectedPawn: state!.Pawn).ConfigureAwait(false)
                : await runtime.QueryNativeReachAsync(participant, target, token, expectedPawn: state!.Pawn).ConfigureAwait(false);

            uncertainCommand = false;

            if (state == null || result.Controller != participant.Controller || result.Pri != participant.Pri ||
                result.Pawn != state.Pawn || result.Goal != target || result.Point != destination ||
                result.WorldGeneration != participant.WorldGeneration)
                throw new InvalidDataException("到达收据不属于本段冻结的 Pawn 与目标，禁止续发。");

            return result;
        }

        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            entered = true;

            if (failed)
                return Finish(AfdevBioBotRouteOutcome.NativeFailure, "此前路线已有未知完成，必须回收原 Job。", true);
            if (point.HasValue != (mode is AfdevBioBotNavigationMode.Point or AfdevBioBotNavigationMode.PointReachability) ||
                point is { } position && (!position.IsValid || goal != 0) || !point.HasValue && goal < 0x10000 ||
                walkStep != null && (mode != AfdevBioBotNavigationMode.Point || walkStep.To != point || !walkStep.From.IsValid))
                return Finish(AfdevBioBotRouteOutcome.TargetInvalid, "目标地址无效，尚未发布任何程序。");

            AfdevBioBotNavigationState previous = await runtime.CheckNavigationAsync(participant, token).ConfigureAwait(false);

            if (previous.RequiresProcessStop)
                return Finish(AfdevBioBotRouteOutcome.NativeFailure, previous.Reason, true);
            if (previous.Active || previous.TaskId != Guid.Empty && previous.Phase != AfdevBioBotNavigationPhase.Stopped)
                return Finish(AfdevBioBotRouteOutcome.Busy, "既有导航尚未退休，本路线不接管其他发布来源。");

            for (int segment = 0; segment < MaximumSegments; segment++)
            {
                token.ThrowIfCancellationRequested();
                hop = 0;
                hopReach = null;
                finalReach = null;

                var readyWait = Stopwatch.StartNew();
                AfdevBioBotNavigationMode executionMode = point.HasValue && !pointChecked
                    ? AfdevBioBotNavigationMode.PointReachability : mode;

                do
                {
                    uncertainCommand = true;
                    state = point is { } destination
                        ? await runtime.StartNavigationAsync(participant, destination, cancellationToken: token,
                            queryOnly: executionMode == AfdevBioBotNavigationMode.PointReachability).ConfigureAwait(false)
                        : await runtime.StartNavigationAsync(participant, goal, cancellationToken: token, mode: executionMode).ConfigureAwait(false);
                    uncertainCommand = false;
                    ownsProgram = state.TaskId != Guid.Empty;

                    // Round settlement may trigger the same stage after publication. Retire the existing task below before replacing or resending it.
                    if (state.Phase != AfdevBioBotNavigationPhase.WaitingForMovement ||
                        state.TaskId != Guid.Empty || state.Active || state.RequiresProcessStop)
                        break;
                    if (readyWait.Elapsed >= TimeSpan.FromSeconds(20))
                        return Finish(AfdevBioBotRouteOutcome.TimedOut, "等待原生 bCanMove 超过二十秒，未写开局或冻结标志。");

                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                while (true);

                if (state.TaskId == Guid.Empty)
                {
                    AfdevBioBotRouteOutcome outcome = state.Phase switch
                    {
                        AfdevBioBotNavigationPhase.TargetInvalid => AfdevBioBotRouteOutcome.TargetInvalid,
                        AfdevBioBotNavigationPhase.WaitingForPawn => AfdevBioBotRouteOutcome.PawnChanged,
                        AfdevBioBotNavigationPhase.BudgetExhausted => AfdevBioBotRouteOutcome.BudgetExhausted,
                        _ => AfdevBioBotRouteOutcome.Busy
                    };

                    return Finish(outcome, state.Reason, state.RequiresProcessStop);
                }

                segments++;

                if (point.HasValue)
                {
                    if (pointPawn != 0 && (state.Pawn != pointPawn || state.PawnGeneration != pointPawnGeneration))
                    {
                        AfdevBioBotNavigationState retired = await RetireAsync().ConfigureAwait(false);

                        return Finish(AfdevBioBotRouteOutcome.PawnChanged, "坐标查询与移动之间 Pawn 已换代，禁止复用旧角色的可达结论。",
                            retired.RequiresProcessStop || retired.Active);
                    }

                    pointPawn = state.Pawn;
                    pointPawnGeneration = state.PawnGeneration;
                }

                Guid taskId = state.TaskId;
                uint pawn = state.Pawn;
                bool motionStarted = false;

                while (state.Active && !state.RequiresProcessStop &&
                    state.Phase is not (AfdevBioBotNavigationPhase.Stalled or AfdevBioBotNavigationPhase.TimedOut
                        or AfdevBioBotNavigationPhase.PawnChanged or AfdevBioBotNavigationPhase.WaitingForMovement
                        or AfdevBioBotNavigationPhase.TargetInvalid))
                {
                    if (observedElapsed?.Elapsed >= TimeSpan.FromSeconds(3))
                    {
                        AfdevBioBotNavigationState retired = await RetireAsync().ConfigureAwait(false);

                        return Finish(AfdevBioBotRouteOutcome.TimedOut, "示范短步已达三秒上限，停止当前动作后交回规划器。",
                            retired.RequiresProcessStop || retired.Active);
                    }

                    if (!motionStarted && state.LatentAction == (executionMode == AfdevBioBotNavigationMode.Point ? 501 : 503))
                    {
                        hop = state.MoveTarget;

                        if (!point.HasValue && hop < 0x10000)
                            throw new InvalidDataException("原生 MoveToward 没有有效 hop，不能启动物理。");

                        uncertainCommand = true;

                        try
                        {
                            await runtime.StartNativeMotionAsync(participant, token).ConfigureAwait(false);
                        }
                        catch (AfdevBioBotActionInterruptedException)
                        {
                            // Very short actions may finish naturally before physics-descriptor publication. Accept only definite same-task completion, still requiring a native arrival receipt.
                            state = await runtime.CheckNavigationAsync(participant, token).ConfigureAwait(false);

                            if (state.TaskId != taskId || state.Pawn != pawn || state.RequiresProcessStop ||
                                state.Phase is not (AfdevBioBotNavigationPhase.Completed or AfdevBioBotNavigationPhase.NoPath))
                                throw;

                            uncertainCommand = false;
                            break;
                        }

                        uncertainCommand = false;
                        motionStarted = true;
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                    state = await runtime.CheckNavigationAsync(participant, token).ConfigureAwait(false);

                    if (state.TaskId != taskId || state.Pawn != pawn)
                        throw new InvalidDataException("路线活动程序或 Pawn 已换代，不重试旧任务。");
                }

                if (state.RequiresProcessStop)
                {
                    await CleanupAsync().ConfigureAwait(false);
                    return Finish(AfdevBioBotRouteOutcome.NativeFailure, state.Reason, true);
                }

                if (state.Phase is AfdevBioBotNavigationPhase.PawnChanged or AfdevBioBotNavigationPhase.WaitingForMovement
                    or AfdevBioBotNavigationPhase.TargetInvalid)
                {
                    AfdevBioBotNavigationState retired = await RetireAsync().ConfigureAwait(false);
                    AfdevBioBotRouteOutcome outcome = state.Phase switch
                    {
                        AfdevBioBotNavigationPhase.PawnChanged => AfdevBioBotRouteOutcome.PawnChanged,
                        AfdevBioBotNavigationPhase.TargetInvalid => AfdevBioBotRouteOutcome.TargetInvalid,
                        _ => AfdevBioBotRouteOutcome.Paused
                    };

                    return Finish(outcome, "原生生命周期暂停当前路线，旧动作已退休。",
                        retired.RequiresProcessStop || retired.Active);
                }

                if (state.Phase is AfdevBioBotNavigationPhase.Stalled or AfdevBioBotNavigationPhase.TimedOut)
                {
                    AfdevBioBotRouteOutcome outcome = state.Phase == AfdevBioBotNavigationPhase.Stalled
                        ? AfdevBioBotRouteOutcome.Stalled : AfdevBioBotRouteOutcome.TimedOut;
                    AfdevBioBotNavigationState retired = await RetireAsync().ConfigureAwait(false);

                    if (retired.RequiresProcessStop || retired.Active)
                        return Finish(outcome, "本段停止没有安全退休：" + retired.Reason, true);
                    if (observedMove)
                        return Finish(outcome, "示范短步未完成，已经退休，本路线不重复恢复试走。");
                    if (retired.Retirement?.NativeCancellation != true || recoveries >= MaximumRecoveries)
                        return Finish(outcome, "本段已停止；没有可继续使用的取消收据，或已达到两次恢复上限。");
                    if (segments == MaximumSegments)
                        return Finish(AfdevBioBotRouteOutcome.BudgetExhausted, "已达到六段路线预算，本段已安全退休。");

                    recoveries++;
                    pointChecked = false;
                    await Task.Delay(250 * recoveries, token).ConfigureAwait(false);
                    continue;
                }

                if (state.Active || state.Phase is not (AfdevBioBotNavigationPhase.Completed or AfdevBioBotNavigationPhase.NoPath))
                    throw new InvalidDataException("路线没有得到本段原生程序的确定完成状态。");

                if (executionMode is AfdevBioBotNavigationMode.DirectReachability or AfdevBioBotNavigationMode.PointReachability)
                {
                    uint marker = point.HasValue ? participant.Controller : goal;

                    if (state.MoveTarget != 0 && state.MoveTarget != marker)
                        throw new InvalidDataException("直接可达查询返回了非冻结目标。");

                    bool reachable = state.MoveTarget == marker;
                    bool allowObservedStep = mode == AfdevBioBotNavigationMode.Point && !reachable && walkStep != null &&
                        runtime.CanStartWalkStep(walkStep, state);
                    bool continuePoint = mode == AfdevBioBotNavigationMode.Point && (reachable || allowObservedStep);

                    if (continuePoint)
                        finalReach = await ReachAsync(0, point).ConfigureAwait(false);

                    AfdevBioBotNavigationState retired = await RetireAsync().ConfigureAwait(false);

                    if (retired.RequiresProcessStop || retired.Active)
                        return Finish(AfdevBioBotRouteOutcome.NativeFailure, "直接可达查询未取得退休收据。", true);
                    if (continuePoint)
                    {
                        if (finalReach!.Reached)
                            return Finish(AfdevBioBotRouteOutcome.Reached, "坐标已满足原生 ReachedPoint，查询安全退休，无需重复移动。");

                        pointChecked = true;

                        if (allowObservedStep)
                        {
                            observedMove = true;
                            observedElapsed = Stopwatch.StartNew();
                            save("route-observed-step", new ExecuteAsyncJson1 { routeId = routeId, walkStep = walkStep, Pawn = state.Pawn, PawnGeneration = state.PawnGeneration,
                                NativeReachability = false, MaximumSeconds = 3, MaximumDistance = 160 });
                        }

                        continue;
                    }

                    return Finish(reachable ? AfdevBioBotRouteOutcome.DirectlyReachable : AfdevBioBotRouteOutcome.NoPath,
                        reachable ? "原生可达查询返回 true；只证明当前位置可直达，不证明到达。" : "原生可达查询返回 false。");
                }

                // A short program may finish before first observation. Once latent execution was observed, always use the original frozen hop.
                if (hop == 0 && !point.HasValue)
                    hop = state.MoveTarget;
                if (hop != 0)
                    hopReach = await ReachAsync(hop).ConfigureAwait(false);

                // When the final target is this hop, reuse the same native return rather than repeat reflection and game-thread waits.
                finalReach = hop == goal && hopReach != null ? hopReach : await ReachAsync(goal, point).ConfigureAwait(false);

                AfdevBioBotNavigationState completed = await RetireAsync().ConfigureAwait(false);

                if (completed.RequiresProcessStop || completed.Active)
                    return Finish(AfdevBioBotRouteOutcome.NativeFailure, "原生查询已返回，但本段退休未确认：" + completed.Reason, true);

                token.ThrowIfCancellationRequested();

                if (finalReach.Reached)
                    return Finish(AfdevBioBotRouteOutcome.Reached, "最终目标的原生到达返回 true，本段程序与物理已安全退休。");
                if (observedMove)
                    return Finish(AfdevBioBotRouteOutcome.NoPath, "示范短步已结束，但原生 ReachedPoint 返回 false，不能记作走通。");
                if (state.Phase == AfdevBioBotNavigationPhase.NoPath)
                    return Finish(AfdevBioBotRouteOutcome.NoPath, "原生寻路或直接可达检查未给出移动目标，最终目标到达返回 false。");

                pointChecked = false;

                // false is this segment's native result. After retirement, FindPath selects the next segment immediately without an artificial pause.
            }

            return Finish(AfdevBioBotRouteOutcome.BudgetExhausted, "六段均已安全退休，尚未取得最终目标的原生到达 true。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            (bool stoppingFailed, string reason) = await CleanupAsync().ConfigureAwait(false);

            return Finish(AfdevBioBotRouteOutcome.Cancelled, "路线已取消。" + reason, stoppingFailed);
        }
        catch (AfdevBioBotActionInterruptedException error)
        {
            (bool stoppingFailed, string reason) = await CleanupAsync().ConfigureAwait(false);

            return Finish(AfdevBioBotRouteOutcome.PawnChanged, error.Message + " " + reason, stoppingFailed);
        }
        catch (Exception error)
        {
            (bool _, string reason) = await CleanupAsync().ConfigureAwait(false);

            return Finish(AfdevBioBotRouteOutcome.NativeFailure, error.Message + " " + reason, true);
        }
        finally
        {
            if (entered)
                gate.Release();
        }
    }
}

/// <summary>
/// Route outcome is independent of native Stop. Reports Reached only when final-target
/// ReachedDestination/ReachedPoint returns true.
/// </summary>
public enum AfdevBioBotRouteOutcome
{
    Reached,
    NoPath,
    TimedOut,
    Stalled,
    TargetInvalid,
    Cancelled,
    BudgetExhausted,
    NativeFailure,
    Busy,
    PawnChanged,
    Paused,
    DirectlyReachable
}

/// <summary>
/// Frozen hop, independent native arrival, and retirement receipts per segment. Canceled segments fabricate
/// no arrival result.
/// </summary>
public sealed record AfdevBioBotRouteStep(int Segment, Guid TaskId, long PawnGeneration, uint Pawn, uint Hop,
    AfdevBioBotNavigationPhase Completion, AfdevBioBotReachResult? HopReach, AfdevBioBotReachResult? FinalReach,
    AfdevBioBotNavigationState Retirement);

/// <summary>
/// Bounded route result for the same controller. RequiresProcessStop=true obliges the host to stop the
/// original Job.
/// </summary>
public sealed record AfdevBioBotRouteResult(Guid RouteId, AfdevBioBotRouteOutcome Outcome, uint Goal,
    int Segments, int Recoveries, bool RequiresProcessStop, string Reason, IReadOnlyList<AfdevBioBotRouteStep> Steps,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc)
{
    public AfdevBioBotNavigationMode Mode { get; init; }
    public AfdevBioBotPoint? Point { get; init; }
    public bool UsedObservedStep { get; init; }
}
