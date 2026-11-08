namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private readonly CancellationTokenSource workStopping = new();
    private readonly SemaphoreSlim stopGate = new(1, 1);
    private AfdevBioBotPreparation? preparedBot;
    private int activationStage;
    private bool commandsStopped;
    private DateTimeOffset activationArmedAt;

    /// <summary>
    /// Checks native automatic-preparation state, installs the exact Timer entry on the game thread, then
    /// publishes frozen-human scanning. Return means only scanning is armed. With no humans, retain zero
    /// counts rather than waiting and deadlocking endpoint publication.
    /// </summary>
    public async Task ArmAsync(AfdevBioBotPlan plan, AfdevBioBotPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(preparation);
        cancellationToken.ThrowIfCancellationRequested();
        workStopping.Token.ThrowIfCancellationRequested();
        CheckBinding();

        AfdevBioBotIdentity identity = plan.Bots[slotIndex];
        AfdevBioBotRuntime owner = rosterOwner ?? throw new InvalidOperationException("Bot 尚未归属本局名册。");

        if (preparedBot == null || preparedBot != preparation || world == null ||
            plan.AllocationId != world.AllocationId || plan.AllocationId != preparation.AllocationId ||
            plan.RoomId == 0 || identity.BotId == Guid.Empty || identity.Uin != "bot-" + identity.BotId.ToString("N")[..12] ||
            identity.Uin != preparation.Uin || identity.Nickname != preparation.Nickname || identity.BotId != preparation.BotId ||
            preparation.InstanceId != workspace.InstanceId || preparation.AttemptId != attemptId ||
            preparation.ProcessId != process.ProcessId || preparation.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            preparation.World != world.World || preparation.GameInfo != world.GameInfo ||
            owner.frozenPlan != plan || owner.preparedRoster.Count != plan.Bots.Length ||
            plan.Humans.IsDefaultOrEmpty || !plan.Humans.Any(human => !human.Observer))
            throw new InvalidDataException("Bot 激活计划与冻结真人、原生准备证据或本次世界不一致。");

        if (Interlocked.CompareExchange(ref activationStage, 1, 0) != 0)
            throw new InvalidOperationException("本次 Bot 激活阶段已经结束，禁止重复尝试。");

        using var arming = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var waiting = new AfdevBioBotWaitingInspector(process);
        AfdevBioBotWaitingEvidence ready;

        do
        {
            arming.Token.ThrowIfCancellationRequested();
            CheckBinding();
            ready = waiting.Capture(world, preparation.Controller);

            if (ready.Ready.Value)
                break;
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Bot 原生 PlayerWaiting 定时器未在期限内完成准备。");

            await Task.Delay(25, arming.Token).ConfigureAwait(false);
        } while (true);

        uint timerEntry = sharedTimerEntry;
        uint registrationCounter = owner.activationCommand + AfdevBioBotActivationHook.RegisteredCount;
        byte[] descriptor = new AfdevBioBotActivationInspector(process).Describe(world, preparation, plan.Humans,
            plan.FighterCapacity, plan.Bots.Length, timerEntry, registrationCounter, slotIndex,
            preparationCommand: preparationCommand);

        await DisarmActivationAsync(arming.Token).ConfigureAwait(false);
        process.WriteMemory(activationCommand, descriptor);

        byte[] observedDescriptor = process.ReadMemory(activationCommand, descriptor.Length);
        int idleAck = (int)AfdevBioBotActivationHook.IdleAck;

        // Before Request publication, the game thread still advances idle acknowledgments. Exclude only that changing counter; every other descriptor field must match completely.
        if (timerEntry < 0x10000 || !observedDescriptor.AsSpan(0, idleAck).SequenceEqual(descriptor.AsSpan(0, idleAck)) ||
            !observedDescriptor.AsSpan(idleAck + 4).SequenceEqual(descriptor.AsSpan(idleAck + 4)))
            throw new InvalidDataException("激活 Timer 代码或描述页安装回读不一致。");

        Save("activation-descriptor", new ArmAsyncJson1
        {
            Command = activationCommand, timerEntry = timerEntry, CommandHex = Convert.ToHexString(descriptor),
            SharedTimer = true, ready = ready
        });
        process.WriteMemory(activationCommand, [2, 0, 0, 0]);
        elapsed.Restart();

        var reader = new QueryInspector(process);

        while (reader.U32(activationCommand + AfdevBioBotTimerHook.InstalledSlot) != 1 || reader.U32(activationCommand) != 0)
        {
            arming.Token.ThrowIfCancellationRequested();
            CheckBinding();

            uint status = reader.U32(activationCommand + AfdevBioBotActivationHook.Status);

            if (status != 0 || elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                SaveActivationFailure(descriptor);
                throw new InvalidDataException($"激活 Timer 安装没有确认完成，状态 {status}；禁止重发。");
            }

            await Task.Delay(10, arming.Token).ConfigureAwait(false);
        }

        arming.Token.ThrowIfCancellationRequested();
        process.WriteMemory(activationCommand, [1, 0, 0, 0]);
        activationArmedAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref activationStage, 2);
        Save("activation-armed", new ArmAsyncJson2
        {
            AllocationId = plan.AllocationId, RoomId = plan.RoomId, BotId = identity.BotId, Uin = identity.Uin,
            HumanSessionIds = plan.Humans.Where(human => !human.Observer).Select(human => human.SessionId).ToArray(),
            Controller = preparation.Controller, Pri = preparation.Pri,
            BotReady = false, AwaitingExactHuman = true, ready = ready, timerEntry = timerEntry,
            Slot = slotIndex, FighterCapacity = plan.FighterCapacity, PlannedBots = plan.Bots.Length
        });
        log("BIO_BOT_ACTIVATION_ARMED 等待冻结真人的原生连接；尚未登记人数。");
    }

    private void SaveActivationFailure(byte[] descriptor)
    {
        var reader = new QueryInspector(process);
        var proofs = new List<object>();
        uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(0xB4, 4));

        for (int index = 0; index < Math.Min(count, AfdevBioBotActivationHook.ProofCapacity); index++)
        {
            int offset = AfdevBioBotActivationHook.ProofOffset + index * 8;
            uint address = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(offset, 4));
            uint expected = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(offset + 4, 4));

            proofs.Add(new SaveActivationFailureJson1 { Address = address, Expected = expected, Actual = reader.U32(address) });
        }

        Save("activation-failed", new SaveActivationFailureJson2
        {
            Command = activationCommand, CommandHex = Convert.ToHexString(process.ReadMemory(activationCommand, AfdevBioBotActivationHook.PageSize)), proofs = proofs
        });
    }

    /// <summary>
    /// Accepts only exact game-thread registration. Waiting for humans is not BotReady; gate failure
    /// reclaims the same host.
    /// </summary>
    private void CheckParticipant()
    {
        workStopping.Token.ThrowIfCancellationRequested();
        CheckBinding();

        if (Volatile.Read(ref activationStage) < 2)
            throw new InvalidOperationException("Bot 尚未发布原生激活扫描。");

        var reader = new QueryInspector(process);
        uint status = reader.U32(activationCommand + AfdevBioBotActivationHook.Status);

        if (status == 0)
        {
            if (DateTimeOffset.UtcNow - activationArmedAt > TimeSpan.FromMinutes(2))
                throw new TimeoutException("Bot 激活未等到冻结真人，结束并回收当前对局。");

            return;
        }

        if (status != AfdevBioBotActivationHook.Registered)
            throw new InvalidDataException($"Bot 原生激活门拒绝，状态 {status}；不补写人数，回收当前对局。");

        if (Interlocked.CompareExchange(ref activationStage, 3, 2) == 2)
        {
            Save("activation-registered", new CheckParticipantJson1
            {
                InstanceId = world!.InstanceId, AttemptId = world.AttemptId, AllocationId = world.AllocationId, WorldGeneration = world.WorldGeneration,
                Controller = preparedBot!.Controller, Pri = preparedBot.Pri, Status = status,
                HumanConnection = reader.U32(activationCommand + 0x20),
                HumanController = reader.U32(activationCommand + 0x24), HumanPri = reader.U32(activationCommand + 0x28),
                ClientVisibilityValidated = false
            });
            log("BIO_BOT_REGISTERED 已按冻结真人登记原生人数；可见性与行为另行验收。");
        }

        ObserveFirstPawn(reader);
    }

    /// <summary>
    /// Called after the host acquires its operation gate and stops preparation. Revoke all command
    /// requests, then await same-game-thread IdleAck. The factory has no independent ACK; a later resource
    /// gate confirms passage through the factory gate. Unknown completion throws for original-Job cleanup.
    /// </summary>
    private async Task StopParticipantAsync(CancellationToken cancellationToken = default)
    {
        workStopping.Cancel();
        await stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (commandsStopped || !process.IsAlive)
                return;

            AfdevBioBotRouteRun? currentRoute;

            lock (navigationGate)
                currentRoute = routeRun;

            if (currentRoute != null)
                await currentRoute.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);

            if (combat != null)
                await combat.StopAsync(cancellationToken).ConfigureAwait(false);

            await DisarmActivationAsync(cancellationToken).ConfigureAwait(false);
            await DisarmOnlineIdentityConfirmationAsync(cancellationToken).ConfigureAwait(false);
            await AfdevBioBotControlInitialization.DisarmAndWaitAsync(process, controlCommand, cancellationToken).ConfigureAwait(false);

            AfdevBioBotNavigation? currentNavigation;
            AfdevBioBotReachQuery? currentReachQuery;

            lock (navigationGate)
            {
                currentNavigation = navigation;
                currentReachQuery = reachQuery;
            }

            if (currentReachQuery != null)
                await currentReachQuery.StopAsync(cancellationToken).ConfigureAwait(false);

            if (currentNavigation != null)
            {
                AfdevBioBotNavigationState state = await currentNavigation.StopAsync(cancellationToken).ConfigureAwait(false);

                if (state.RequiresProcessStop)
                    throw new InvalidOperationException("Bot 导航 latent 仍活跃或完成未知；必须回收原进程和 Job。");
            }

            var reader = new QueryInspector(process);
            bool anyPublished = new[] { factoryCommand, resourceCommand, lifecycleCommand }
                .Any(command => reader.U32(command) != 0 || reader.U32(command + 4) != 0 || reader.U32(command + 8) != 0);

            // Cancel the earlier factory request first. Resource ACK follows the factory gate in the same wrapper and confirms only the currently owned process.
            process.WriteMemory(factoryCommand, [0, 0, 0, 0]);
            process.WriteMemory(resourceCommand, [0, 0, 0, 0]);
            process.WriteMemory(lifecycleCommand, [0, 0, 0, 0]);

            CommandIdleAckReceipt? resourcesIdle = null;
            CommandIdleAckReceipt? lifecycleIdle = null;

            if (anyPublished)
            {
                resourcesIdle = await ResourceCallRun.DisarmAndWaitAsync(process, resourceCommand, cancellationToken).ConfigureAwait(false);
                lifecycleIdle = await ResourceCallRun.DisarmAndWaitAsync(process, lifecycleCommand, cancellationToken).ConfigureAwait(false);
            }

            foreach (uint command in new[] { factoryCommand, resourceCommand, lifecycleCommand })
            {
                uint requested = reader.U32(command);
                uint started = reader.U32(command + 4);
                uint complete = reader.U32(command + 8);
                uint status = reader.U32(command + 12);

                if (requested != 0 || (started != 0 && (complete != 1 || status != 1)))
                    throw new InvalidOperationException("Bot 原生命令停止后仍存在未知完成状态；必须回收原进程和 Job。");
            }

            commandsStopped = true;
            Save("commands-stopped", new StopParticipantAsyncJson1
            {
                InstanceId = workspace.InstanceId, attemptId = attemptId, ProcessId = process.ProcessId, CreationTimeUtc = process.CreationTimeUtc,
                AnyCommandWasPublished = anyPublished, resourcesIdle = resourcesIdle, lifecycleIdle = lifecycleIdle,
                PagesRetainedUntilOwnedJobStops = true, BotReady = false
            });
        }
        finally
        {
            stopGate.Release();
        }
    }

    private async Task DisarmActivationAsync(CancellationToken cancellationToken)
    {
        var reader = new QueryInspector(process);
        uint before = reader.U32(activationCommand + AfdevBioBotActivationHook.IdleAck);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        process.WriteMemory(activationCommand, [0, 0, 0, 0]);

        while (process.IsAlive && reader.U32(activationCommand + AfdevBioBotActivationHook.IdleAck) == before)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("Bot 激活扫描未确认退出，必须由原宿主回收 Job。");

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }
}
