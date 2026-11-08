using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private AfdevBioBotRuntime[] rosterSlots = [];
    private AfdevBioBotRuntime? rosterOwner;
    private IReadOnlyList<AfdevBioBotPreparation> preparedRoster = [];
    private AfdevBioBotPlan? frozenPlan;
    private int slotIndex;
    private int rosterPreparationStarted;
    private int rosterBehaviorStarted;
    private uint sharedTimerEntry;

    /// <summary>
    /// All bots whose resources were prepared for this start. Published once, without refilling after
    /// death, infection, or human departure.
    /// </summary>
    public IReadOnlyList<AfdevBioBotPreparation> Preparations => (rosterOwner ?? this).preparedRoster;

    /// <summary>
    /// Prepares the full roster in frozen order alongside human loading. Resource gates use this round's
    /// native-preparation boundary; a complete roster does not depend on spawned Pawns. Empty rosters
    /// create no native objects. Any slot failure requires the same host to stop its Job, never skipping or
    /// refilling a failed slot.
    /// </summary>
    public async Task<IReadOnlyList<AfdevBioBotPreparation>> PrepareRosterAsync(AfdevNativeEvidence boundWorld,
        AfdevBioBotPlan plan, CancellationToken cancellationToken = default, bool initializeNativeControl = true)
    {
        ArgumentNullException.ThrowIfNull(boundWorld);
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();

        if (rosterOwner != this || rosterSlots.Length == 0 || plan.AllocationId != boundWorld.AllocationId ||
            plan.FighterCapacity != boundWorld.FighterCapacity || plan.Bots.Length > rosterSlots.Length)
            throw new InvalidDataException("Bot 名册不属于本次 allocation 已核实的原生容量或实例槽集合。");

        int humanCount = plan.Humans.Count(human => !human.Observer);

        if (Interlocked.CompareExchange(ref rosterPreparationStarted, 1, 0) != 0)
            throw new InvalidOperationException("本实例的冻结 Bot 名册已开始准备，不能重新绑定或补人。");

        await BeginRosterPreparationAsync(boundWorld, plan, cancellationToken).ConfigureAwait(false);

        var prepared = new List<AfdevBioBotPreparation>(plan.Bots.Length);

        foreach (AfdevBioBotIdentity identity in plan.Bots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workStopping.Token.ThrowIfCancellationRequested();

            AfdevBioBotRuntime slot = rosterSlots[prepared.Count];
            AfdevBioBotPreparation participant = await slot.PrepareParticipantAsync(boundWorld, plan, identity,
                cancellationToken, initializeNativeControl).ConfigureAwait(false);

            prepared.Add(participant);
        }

        if (prepared.Select(bot => bot.Controller).Distinct().Count() != prepared.Count ||
            prepared.Select(bot => bot.Pri).Distinct().Count() != prepared.Count ||
            prepared.Select(bot => bot.PlayerId).Distinct().Count() != prepared.Count)
            throw new InvalidDataException("原生 Bot 名册出现重复的 Controller、PRI 或 PlayerID。");

        preparedRoster = Array.AsReadOnly(prepared.ToArray());
        SaveRoster("roster-prepared", new PrepareRosterAsyncJson1
        {
            InstanceId = boundWorld.InstanceId, AttemptId = boundWorld.AttemptId, AllocationId = boundWorld.AllocationId, WorldGeneration = boundWorld.WorldGeneration,
            RoomId = plan.RoomId, FighterCapacity = plan.FighterCapacity, FrozenHumanFighters = humanCount,
            Observers = plan.Humans.Count(human => human.Observer), Bots = preparedRoster,
            WorldReady = true, BotRosterPrepared = true, PawnReady = false
        });
        log($"BIO_BOT_ROSTER_PREPARED capacity={plan.FighterCapacity} humans={humanCount} bots={preparedRoster.Count}; 资源准备完成，真人加载名额保留。");
        return preparedRoster;
    }

    /// <summary>
    /// Retains the single-object diagnostic entry point, accepting only a frozen roster containing exactly
    /// one bot.
    /// </summary>
    public async Task<AfdevBioBotPreparation> PrepareAsync(AfdevNativeEvidence boundWorld, AfdevBioBotPlan plan,
        CancellationToken cancellationToken = default, bool initializeNativeControl = false)
    {
        if (plan.Bots.Length != 1)
            throw new InvalidOperationException("单对象准备入口仅接受一 Bot 名册；正式补人使用 PrepareRosterAsync。");

        IReadOnlyList<AfdevBioBotPreparation> prepared = await PrepareRosterAsync(boundWorld, plan,
            cancellationToken, initializeNativeControl).ConfigureAwait(false);

        return prepared[0];
    }

    /// <summary>
    /// After full resource preparation, shares one Timer entry for slot registration while preserving
    /// independent completion state per slot.
    /// </summary>
    public async Task ArmRosterAsync(AfdevBioBotPlan plan, CancellationToken cancellationToken = default)
    {
        if (rosterOwner != this || frozenPlan != plan || preparedRoster.Count != plan.Bots.Length)
            throw new InvalidOperationException("Bot 名册未完整准备，不能发布部分名单的原生激活。");

        for (int index = 0; index < preparedRoster.Count; index++)
            await rosterSlots[index].ArmAsync(plan, preparedRoster[index], cancellationToken).ConfigureAwait(false);

        await CompleteRosterPreparationAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The host's sole behavior task. Each bot owns its action source with staggered samples. A slot
    /// failure immediately cancels others; propagate failure only after all fire stops and navigation
    /// retires, leaving no background task outside this round's ownership.
    /// </summary>
    public async Task RunRosterBehaviorAsync(CancellationToken cancellationToken = default, bool recordPlayerRoutes = false)
    {
        if (rosterOwner != this || frozenPlan == null || preparedRoster.Count == 0 ||
            preparedRoster.Count != frozenPlan.Bots.Length ||
            rosterSlots.Take(preparedRoster.Count).Any(slot => Volatile.Read(ref slot.activationStage) < 2))
            throw new InvalidOperationException("完整 Bot 名册尚未发布激活，不能启动集合行为。");
        if (Interlocked.CompareExchange(ref rosterBehaviorStarted, 1, 0) != 0)
            throw new InvalidOperationException("同一对局只能持有一个 Bot 集合行为任务。");

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);

        async Task RunSlotAsync(int index)
        {
            try
            {
                // Stagger only low-frequency decision phases, never resource preparation or native spawning.
                if (index != 0)
                    await Task.Delay(index * 17, running.Token).ConfigureAwait(false);

                await rosterSlots[index].RunContinuousBehaviorAsync(preparedRoster[index], running.Token).ConfigureAwait(false);

                if (!running.IsCancellationRequested)
                    throw new InvalidOperationException($"Bot 槽 {index} 的持续行为意外退出。");
            }
            catch
            {
                running.Cancel();
                throw;
            }
        }

        Task[] tasks = Enumerable.Range(0, preparedRoster.Count).Select(RunSlotAsync).ToArray();

        if (recordPlayerRoutes)
            tasks = [.. tasks, RecordRoutesAsync()];

        async Task RecordRoutesAsync()
        {
            try
            {
                var recorder = new AfdevBioPlayerRouteRecorder(process, world!, workspace, frozenPlan.Humans,
                    log, CheckBinding);

                await recorder.RunAsync(running.Token).ConfigureAwait(false);

                // The recorder has closed the file. Parse anonymous geometry only, without accessing a possibly retired native Pawn.
                AfdevBioBotWalkRoutes learned = await GetWalkRoutesAsync(CancellationToken.None).ConfigureAwait(false);

                await learned.ImportRecordingAsync(Path.Combine(workspace.Directory, "logs", "bio-bot", "player-routes.jsonl"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (running.IsCancellationRequested) { }
            catch (Exception error)
            {
                // The recorder reads native state only. Its failure must not leave WhenAll waiting on live gameplay tasks and stall cleanup.
                log($"BIO_PLAYER_ROUTE_RECORDING_DISABLED kind={error.GetType().Name}; 示范记录已停用。");
            }
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || workStopping.IsCancellationRequested)
        {
            // WhenAll already awaited retirement of every started task. Normal external cancellation is not a behavior failure.
        }
    }

    /// <summary>
    /// Accepts each slot's own native registration only. One completed bot cannot stand in for another.
    /// </summary>
    public void Check()
    {
        workStopping.Token.ThrowIfCancellationRequested();

        if (rosterOwner != this)
        {
            CheckParticipant();
            return;
        }

        foreach (AfdevBioBotRuntime slot in rosterSlots.Take(preparedRoster.Count))
            slot.CheckParticipant();
    }

    /// <summary>
    /// Revokes new work for the entire set before confirming retirement per slot. Native pages remain until
    /// the same Job exits.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        AfdevBioBotRuntime[] slots = rosterOwner == this ? rosterSlots : [this];

        foreach (AfdevBioBotRuntime slot in slots)
            slot.workStopping.Cancel();

        // Close the unreleased start gate first so old registration proofs cannot start an incomplete roster during per-slot retirement.
        if (rosterOwner == this)
            await StopRosterPreparationAsync(cancellationToken).ConfigureAwait(false);

        foreach (AfdevBioBotRuntime slot in slots.Where(slot => Volatile.Read(ref slot.preparationStarted) != 0))
            await slot.StopParticipantAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SaveRoster(string name, object value)
    {
        string directory = Path.Combine(workspace.Directory, "logs", "bio-bot");

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
