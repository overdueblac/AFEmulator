namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private uint combatCommand;
    private AfdevBioBotCombat? combat;

    /// <summary>
    /// The bot's sole behavior source, coordinating patrol, pursuit, and native combat against frozen
    /// humans and prepared bots in this match. AFDEV owns round/Pawn changes. Host cancellation waits for
    /// stopped fire and retired navigation.
    /// </summary>
    public async Task RunContinuousBehaviorAsync(AfdevBioBotPreparation participant,
        CancellationToken cancellationToken = default)
    {
        AfdevBioBotCombat nativeCombat = GetCombat(participant);

        if (Interlocked.CompareExchange(ref patrolStarted, 1, 0) != 0)
            throw new InvalidOperationException("同一 BIO Bot 槽只能有一个持续行为调度任务。");

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);
        AfdevBioBotRuntime owner = rosterOwner ?? throw new InvalidOperationException("Bot 尚未归属本局名册。");
        AfdevBioBotPlan plan = owner.frozenPlan ?? throw new InvalidOperationException("Bot 尚无冻结开赛名册。");
        var engagement = new AfdevBioBotEngagement(nativeCombat, process, world!, participant,
            () => owner.preparedRoster, plan.Humans, Save, CheckBinding);
        AfdevBioBotWalkRoutes learned = await GetWalkRoutesAsync(running.Token).ConfigureAwait(false);
        var patrol = new AfdevBioBotPatrol(this, process, world!, participant, Save, CheckBinding, engagement, learned);

        await patrol.RunAsync(running.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks original-direction combat entry points once in an owned no-human diagnostic world. The caller
    /// starts under native rules and owns final cleanup. Creates no targets and writes no damage/teams.
    /// Return proves entry execution and stopped fire, not hits or human-play acceptance.
    /// </summary>
    public async Task<AfdevBioBotCombatEntryCheck> CheckCombatEntryAsync(AfdevBioBotPreparation participant,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref activationStage) != 0)
            throw new InvalidOperationException("原方向战斗检查只允许未发布真人激活的自有检查世界。");

        AfdevBioBotCombat nativeCombat = GetCombat(participant);

        if (Interlocked.CompareExchange(ref patrolStarted, 1, 0) != 0)
            throw new InvalidOperationException("战斗入口检查不能与持续行为共享发布来源。");

        using var checking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        AfdevBioBotCombatSnapshot? snapshot;

        try
        {
            do
            {
                checking.Token.ThrowIfCancellationRequested();
                snapshot = nativeCombat.ReadSnapshot();

                if (snapshot is { CanAct: true })
                    break;
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("短战斗检查未等到原生可行动的 Pawn 和武器。");

                await Task.Delay(100, checking.Token).ConfigureAwait(false);
            } while (true);

            async Task<bool> Call(AfdevBioBotCombatAction action)
            {
                AfdevBioBotCombatResult result = await nativeCombat.ExecuteAsync(action, snapshot,
                    cancellationToken: checking.Token).ConfigureAwait(false);

                if (!result.NativeExecuted || result.Interrupted)
                    throw new InvalidOperationException($"短战斗检查入口未完成：{action}。");

                return true;
            }

            bool aimed = await Call(AfdevBioBotCombatAction.Aim).ConfigureAwait(false);
            bool fired = await Call(AfdevBioBotCombatAction.StartFire).ConfigureAwait(false);

            await Task.Delay(400, checking.Token).ConfigureAwait(false);

            bool stopped = await Call(AfdevBioBotCombatAction.StopFire).ConfigureAwait(false);
            bool reloaded = await Call(AfdevBioBotCombatAction.Reload).ConfigureAwait(false);

            await nativeCombat.StopAsync(CancellationToken.None).ConfigureAwait(false);

            var result = new AfdevBioBotCombatEntryCheck(snapshot.Pawn.Address, snapshot.Weapon.Address,
                aimed, fired, stopped, reloaded, DateTimeOffset.UtcNow);

            Save("combat-entry-check", result);
            return result;
        }
        finally
        {
            await nativeCombat.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private AfdevBioBotCombat GetCombat(AfdevBioBotPreparation participant)
    {
        lock (navigationGate)
        {
            _ = GetNavigation(participant);

            if (combatCommand == 0)
                throw new InvalidOperationException("原生战斗命令页尚未安装。");

            return combat ??= new AfdevBioBotCombat(process, world!, participant, onlineBotId,
                onlineIsolationPage, combatCommand, Save, CheckBinding);
        }
    }
}

/// <summary>
/// Return evidence for one short combat-entry check, without acceptance claims for hits, damage, infection,
/// or human interaction.
/// </summary>
public sealed record AfdevBioBotCombatEntryCheck(uint Pawn, uint Weapon, bool AimReturned,
    bool FireReturned, bool StopReturned, bool ReloadReturned, DateTimeOffset StoppedAtUtc);
