using System.Buffers.Binary;
using System.Diagnostics;

namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private uint preparationCommand;
    private uint preparationStartEntry;
    private int preparationGateStarted;
    private int preparationGateInstalled;
    private int preparationGateCompleted;
    private int preparationGateReleased;
    private AfdevNativeEvidence? preparationWorld;

    /// <summary>
    /// Installs only this round's lightweight preparation gate after world readiness and before human
    /// endpoints. A completed installation for the same binding may reuse confirmation; incomplete/failed
    /// requests cannot resend. Resource preparation runs alongside human loading after return.
    /// </summary>
    public async Task BeginRosterPreparationAsync(AfdevNativeEvidence boundWorld, AfdevBioBotPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundWorld);
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        workStopping.Token.ThrowIfCancellationRequested();

        if (plan.Bots.Length == 0)
            return;
        if (rosterOwner != this || plan.AllocationId != boundWorld.AllocationId ||
            plan.FighterCapacity != boundWorld.FighterCapacity || plan.Bots.Length > rosterSlots.Length ||
            boundWorld.InstanceId != workspace.InstanceId || boundWorld.AttemptId != attemptId ||
            boundWorld.ProcessId != process.ProcessId || boundWorld.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            preparationCommand < 0x10000 || preparationStartEntry < 0x10000 || sharedTimerEntry < 0x10000)
            throw new InvalidDataException("BIO 准备门缺少本实例、启动尝试、冻结世界与原生页所有权。");

        if (Interlocked.CompareExchange(ref preparationGateStarted, 1, 0) != 0)
        {
            if (preparationWorld == boundWorld && frozenPlan == plan && Volatile.Read(ref preparationGateInstalled) == 1)
            {
                CheckRosterPreparation();
                return;
            }

            throw new InvalidOperationException("BIO 准备门已有未知或不同绑定的安装请求，禁止重发。");
        }

        preparationWorld = boundWorld;
        world = boundWorld;
        frozenPlan = plan;

        byte[] descriptor = new AfdevBioBotPreparationInspector(process).Describe(boundWorld, plan,
            preparationCommand, preparationStartEntry, sharedTimerEntry,
            rosterSlots.Select(slot => slot.activationCommand).ToArray());
        var elapsed = Stopwatch.StartNew();
        var reader = new QueryInspector(process);

        process.WriteMemory(preparationCommand, descriptor);

        byte[] observed = process.ReadMemory(preparationCommand, descriptor.Length);
        int idle = (int)AfdevBioBotPreparationHook.IdleAck;

        if (!observed.AsSpan(0, idle).SequenceEqual(descriptor.AsSpan(0, idle)) ||
            !observed.AsSpan(idle + 4).SequenceEqual(descriptor.AsSpan(idle + 4)))
            throw new IOException("BIO 准备协调描述页写入回读不一致，必须回收当前 Job。");

        SaveRoster("preparation-gate-described", new BeginRosterPreparationAsyncJson1
        {
            InstanceId = boundWorld.InstanceId, AttemptId = boundWorld.AttemptId, AllocationId = boundWorld.AllocationId, WorldGeneration = boundWorld.WorldGeneration,
            RoomId = plan.RoomId, FighterCapacity = plan.FighterCapacity, PlannedBots = plan.Bots.Length,
            preparationCommand = preparationCommand, preparationStartEntry = preparationStartEntry, sharedTimerEntry = sharedTimerEntry,
            CommandHex = Convert.ToHexString(descriptor), WorldReady = true, BotRosterPrepared = false, PawnReady = false
        });
        process.WriteMemory(preparationCommand + AfdevBioBotPreparationHook.Request, [1, 0, 0, 0]);

        while (reader.U32(preparationCommand + AfdevBioBotPreparationHook.Status) != AfdevBioBotPreparationHook.Installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workStopping.Token.ThrowIfCancellationRequested();

            uint status = reader.U32(preparationCommand + AfdevBioBotPreparationHook.Status);

            if (!process.IsAlive || status != 0 || elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException($"BIO 原生准备门安装未确认，状态 {status}；禁止重装当前实例。");

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref preparationGateInstalled, 1);
        SaveRoster("preparation-gate-installed", new BeginRosterPreparationAsyncJson2
        {
            AtUtc = DateTimeOffset.UtcNow, AllocationId = boundWorld.AllocationId, RoomId = plan.RoomId, FighterCapacity = plan.FighterCapacity,
            PlannedBots = plan.Bots.Length, ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            WorldReady = true, BotRosterPrepared = false, PawnReady = false,
            OriginalTimerContinues = true, NativeStartRequestHeld = true
        });
        log("BIO_BOT_PREPARATION_GATE_INSTALLED 世界已就绪，可发布真人端点；原生开局等待本局名册。");
    }

    /// <summary>
    /// After every independent slot publishes resources and activation, lets native entries verify
    /// registration and current humans themselves.
    /// </summary>
    private Task CompleteRosterPreparationAsync(AfdevBioBotPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workStopping.Token.ThrowIfCancellationRequested();

        if (rosterOwner != this || frozenPlan != plan || preparationWorld == null ||
            Volatile.Read(ref preparationGateInstalled) != 1 || preparedRoster.Count != plan.Bots.Length ||
            rosterSlots.Take(plan.Bots.Length).Any(slot => Volatile.Read(ref slot.activationStage) < 2))
            throw new InvalidOperationException("BIO 名册或独立激活槽尚未完整发布，不能放行原生准备条件。");
        if (Interlocked.CompareExchange(ref preparationGateCompleted, 1, 0) != 0)
            throw new InvalidOperationException("BIO 名册准备完成信号已经发布，不能重复提交。");

        AfdevBioBotPreparationInspector.RequirePreparing(process, preparationWorld, preparationCommand);
        process.WriteMemory(preparationCommand + AfdevBioBotPreparationHook.RosterComplete, [1, 0, 0, 0]);
        SaveRoster("preparation-resources-complete", new CompleteRosterPreparationAsyncJson1
        {
            AtUtc = DateTimeOffset.UtcNow, AllocationId = preparationWorld.AllocationId, RoomId = plan.RoomId,
            PlannedBots = plan.Bots.Length, ResourcesAndActivationPublished = true,
            AwaitingNativeRegistrationAndHuman = true, PawnReady = false
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Also callable during preparation. Checks round-gate receipts only; absent Pawns or humans are not
    /// failures.
    /// </summary>
    public void CheckRosterPreparation()
    {
        if (Volatile.Read(ref preparationGateStarted) == 0)
            return;

        var reader = new QueryInspector(process);
        uint status = reader.U32(preparationCommand + AfdevBioBotPreparationHook.Status);

        if (!process.IsAlive || preparationWorld == null ||
            reader.U32(0x02066BF8) != preparationWorld.World ||
            status is not (AfdevBioBotPreparationHook.Installed or AfdevBioBotPreparationHook.Released))
            throw new InvalidDataException($"BIO 准备协调失去本局世界或原生确认，状态 {status}。");

        if (status == AfdevBioBotPreparationHook.Released &&
            Interlocked.CompareExchange(ref preparationGateReleased, 1, 0) == 0)
        {
            SaveRoster("preparation-native-start-released", new CheckRosterPreparationJson1
            {
                ObservedAtUtc = DateTimeOffset.UtcNow, AllocationId = preparationWorld.AllocationId,
                PlannedBots = frozenPlan!.Bots.Length,
                BlockedStartRequests = reader.U32(preparationCommand + AfdevBioBotPreparationHook.BlockedStarts),
                BotRosterPrepared = true, NativeStartRequestReleased = true, PawnReady = false
            });
            log("BIO_BOT_ROSTER_PREPARED 已登记完整名册并核实当前真人；原生入口继续开局与出生。");
        }
    }

    /// <summary>
    /// Revokes this round's gate and waits for game-thread idle confirmation, retaining pages still
    /// referenced by original UFunctions.
    /// </summary>
    private async Task StopRosterPreparationAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref preparationGateStarted) == 0 || !process.IsAlive)
            return;

        var reader = new QueryInspector(process);
        uint before = reader.U32(preparationCommand + AfdevBioBotPreparationHook.IdleAck);
        var elapsed = Stopwatch.StartNew();

        process.WriteMemory(preparationCommand + AfdevBioBotPreparationHook.Request, [0, 0, 0, 0]);

        while (process.IsAlive && reader.U32(preparationCommand + AfdevBioBotPreparationHook.IdleAck) == before)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("BIO 准备门未确认停止，必须回收原 Job。");

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        SaveRoster("preparation-gate-stopped", new StopRosterPreparationAsyncJson1
        {
            AtUtc = DateTimeOffset.UtcNow, World = preparationWorld?.World,
            IdleAcknowledged = process.IsAlive,
            PagesRetainedUntilOwnedJobStops = true
        });
    }
}
