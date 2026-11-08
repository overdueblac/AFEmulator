using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevProcessHost
{
    private Task? reviveCoinWork;

    /// <summary>
    /// Installs this match's coin-deduction bridge after first binding and before endpoint publication. All
    /// PVE maps share the same transaction entry point.
    /// </summary>
    internal async Task<AfdevNativeEvidence> BindWithReviveCoinsAsync(MatchStartSnapshot snapshot,
        IPveReviveCoinStore? store, CancellationToken cancellationToken)
    {
        var evidence = await BindAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (!PveReviveCoins.Supports(snapshot.Descriptor.ModeId)) return evidence;
        if (store is null) throw new InvalidOperationException("PVE 开赛缺少权威复活币事务入口。");

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (process is null || State != AfdevRuntimeState.SessionReady || reviveCoinWork is not null)
                throw new InvalidOperationException("PVE 复活币只能安装在本次首次绑定的就绪世界。");

            var runtime = new AfdevPveReviveCoinRuntime(process, snapshot, evidence, store, Report, botRuntime);

            reviveCoinWork = Task.Run(() => runtime.RunAsync(stopRequested.Token));

            return evidence;
        }
        finally { operationGate.Release(); }
    }

    private async Task StopReviveCoinsAsync()
    {
        if (reviveCoinWork is null) return;

        try { await reviveCoinWork.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stopRequested.IsCancellationRequested) { }
        catch (Exception error) { Report($"PVE_REVIVE_STOPPED kind={error.GetType().Name}"); }
    }
}
