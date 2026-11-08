using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevProcessHost
{
    private readonly CancellationTokenSource botWorkStopping = new();
    private readonly bool botBridgeEnabled;
    private readonly bool recordPlayerRoutes;
    private AfdevBioBotRuntime? botRuntime;
    private IReadOnlyList<AfdevBioBotPreparation> botPreparations = [];
    private Task? botPreparationTask;
    private Task? botWorkTask;

    /// <summary>
    /// Resource evidence for the first bot, retained for diagnostic compatibility. BotPreparations contains
    /// the complete roster.
    /// </summary>
    public AfdevBioBotPreparation? BotPreparation => BotPreparations.FirstOrDefault();

    /// <summary>
    /// The fully prepared bot roster frozen for this start. Does not prove that humans have seen or can
    /// attack the bots.
    /// </summary>
    public IReadOnlyList<AfdevBioBotPreparation> BotPreparations => Volatile.Read(ref botPreparations);

    /// <summary>
    /// This match's separate bounded preparation task. Completion means roster resources and activation are
    /// armed; it does not wait for Pawns requiring native match start.
    /// </summary>
    public Task? BotPreparationTask => botPreparationTask;

    private void InstallBotBridgeSuspended()
    {
        if (!botBridgeEnabled)
            return;

        botRuntime = AfdevBioBotRuntime.InstallSuspended(process!, workspace!, attemptId, ReportNative);
    }

    private async Task BeginBoundBotPreparationAsync(AfdevBioBotPlan? plan, AfdevNativeEvidence evidence, CancellationToken token)
    {
        if (plan == null)
        {
            if (botRuntime != null && AfdevMapCatalog.SupportsBioBots(evidence.ModeId, evidence.MapId))
                throw new InvalidOperationException("已启用 Bot 的物理实例缺少冻结的真实回合计划。");

            // Warmup installs only dormant entry points. Other modes publish no BIO roster or behavior commands; PvP bots use their own native preparer.
            string message = AfdevMapCatalog.SupportsPvp(evidence.ModeId, evidence.MapId)
                ? "BIO_CUSTOM_BOTS_DISABLED" : "PLAYER_BOTS_DISABLED";

            ReportNative($"{message} mode=0x{evidence.ModeId:X4} map=0x{evidence.MapId:X4}");
            return;
        }

        if (botRuntime == null || plan.AllocationId != allocationId || plan.RoomId != roomId)
            throw new InvalidOperationException("Bot 计划与本次物理实例和真实回合不匹配。");

        if (plan.Bots.IsEmpty)
        {
            Report($"BIO_BOT_ROSTER_EMPTY capacity={plan.FighterCapacity}; 满真人房不创建 Bot 或启动行为。");
            return;
        }

        await botRuntime.BeginRosterPreparationAsync(evidence, plan, token).ConfigureAwait(false);
    }

    private void StartBoundBotWork(AfdevBioBotPlan? plan, AfdevNativeEvidence evidence)
    {
        if (plan == null || plan.Bots.IsEmpty)
            return;
        if (botWorkTask != null || botRuntime == null)
            throw new InvalidOperationException("本局 Bot 工作已启动或缺少原生准备器。");

        AfdevBioBotRuntime runtime = botRuntime;

        // Do not capture Bind's short-lived cancellation source. Published work belongs to the original allocation and is canceled and awaited together at shutdown.
        botPreparationTask = Task.Run(() => PrepareBoundBotsAsync(runtime, plan, evidence));
        botWorkTask = RunBoundBotWorkAsync(runtime, botPreparationTask);
    }

    private async Task PrepareBoundBotsAsync(AfdevBioBotRuntime runtime, AfdevBioBotPlan plan, AfdevNativeEvidence evidence)
    {
        using var preparing = CancellationTokenSource.CreateLinkedTokenSource(stopRequested.Token, botWorkStopping.Token);
        TimeSpan timeout = TimeSpan.FromSeconds(plan.Bots.Length * 45);

        preparing.CancelAfter(timeout);
        ReportNative($"BIO_BOT_PREPARATION_STARTED bots={plan.Bots.Length} timeout_seconds={timeout.TotalSeconds:F0}; 真人端点无需等待此任务。");

        try
        {
            IReadOnlyList<AfdevBioBotPreparation> prepared = await runtime.PrepareRosterAsync(evidence, plan,
                preparing.Token).ConfigureAwait(false);

            await runtime.ArmRosterAsync(plan, preparing.Token).ConfigureAwait(false);
            preparing.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref botPreparations, prepared);
            RecordBotRosterPrepared(prepared.Count);
        }
        catch (OperationCanceledException error) when (!stopRequested.IsCancellationRequested &&
            !botWorkStopping.IsCancellationRequested && preparing.IsCancellationRequested)
        {
            throw new TimeoutException($"BIO Bot 名册未在独立的 {timeout.TotalSeconds:F0} 秒期限内准备完成。", error);
        }
    }

    private async Task RunBoundBotWorkAsync(AfdevBioBotRuntime runtime, Task preparation)
    {
        try
        {
            await preparation.ConfigureAwait(false);
            botWorkStopping.Token.ThrowIfCancellationRequested();
            ReportNative($"BIO_BOT_BEHAVIOR_STARTED bots={BotPreparations.Count}; 等待原生出生、阵营和小局状态。");
            await runtime.RunRosterBehaviorAsync(botWorkStopping.Token, recordPlayerRoutes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (botWorkStopping.IsCancellationRequested || stopRequested.IsCancellationRequested)
        {
            // The original host still retires published commands. Normal room departure or shutdown is not preparation failure.
        }
        catch (Exception failure)
        {
            string phase = preparation.IsCompletedSuccessfully ? "behavior" : "preparation";
            string reason = failure.Message.Replace('\r', ' ').Replace('\n', ' ');

            // This is native bot work for this match only, without account authentication. Retain the reason and call site to locate the first failure after cleanup.
            ReportNative($"BIO_BOT_WORK_FAILED phase={phase} kind={failure.GetType().Name} reason={reason}; 由原 allocation 健康监督撤销并回收。");
            WriteAtomic("bot-work-failure.json", new RunBoundBotWorkAsyncJson1
            {
                InstanceId = InstanceId, attemptId = attemptId, allocationId = allocationId, roomId = roomId, ObservedAtUtc = DateTimeOffset.UtcNow,
                Phase = phase, Kind = failure.GetType().FullName, Message = failure.Message, StackTrace = failure.StackTrace
            });
            throw;
        }
    }

    private void CheckBotWork()
    {
        if (botRuntime == null || botWorkTask == null || botWorkStopping.IsCancellationRequested)
            return;

        if (botWorkTask.IsCompleted)
        {
            botWorkTask.GetAwaiter().GetResult();
            throw new InvalidOperationException("BIO Bot 工作任务提前退出，结束并回收本局。");
        }

        botRuntime.CheckRosterPreparation();

        // A slot still preparing has no activation result; do not validate it as an already armed participant.
        if (botPreparationTask is { IsCompletedSuccessfully: true })
        {
            botRuntime.Check();

            if (botRuntime.FirstPawnReadyAtUtc is { } observed)
                RecordFirstPawnReady(observed);
        }
    }

    private async Task WaitForBotWorkAsync()
    {
        if (botWorkTask != null)
            await botWorkTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Stops new bot work and waits for native commands before revoking eligibility and relays. Failure
    /// retains the original host, slot, and round for cleanup by the same object; unfinished commands
    /// cannot transfer to a new instance.
    /// </summary>
    public async Task StopBotWorkAsync()
    {
        botWorkStopping.Cancel();
        await operationGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (botRuntime != null && process is { IsAlive: true })
            {
                try
                {
                    await WaitForBotWorkAsync().ConfigureAwait(false);
                    await botRuntime.StopAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    // Unknown commands or active latent work cannot leave old objects accessible after revocation. Reclaim this Job directly;
                    // do not reenter StopAsync holding the same gate. The original allocation releases the slot during later shutdown.
                    Report($"BIO_BOT_STOP_REQUIRES_PROCESS_STOP kind={failure.GetType().Name}");
                    await CleanupAttemptAsync().ConfigureAwait(false);
                }
            }
        }
        finally { operationGate.Release(); }
    }
}
