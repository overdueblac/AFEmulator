namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevMatchAllocator
{
    private readonly Dictionary<int, WarmInstance> instances = [];
    private readonly CancellationTokenSource warmStopping = new();
    private AfdevSharedResources? sharedResources;
    private Task supervisor = Task.CompletedTask;
    private bool warmupStarted;
    private bool warmupFailed;
    private int warmFailureCount;

    /// <summary>The number of claimable warm engines, not existing BIO maps or player grants.</summary>
    public int WarmAvailableCount
    {
        get { lock (gate) return instances.Values.Count(instance => instance.Available); }
    }

    /// <summary>
    /// Called during normal server startup, never by check commands. Repeated calls do not create another
    /// task group.
    /// </summary>
    public void StartWarmup()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (warmupStarted) return;

            validation.Options.Validate();
            warmupStarted = true;
            supervisor = Task.Run(SuperviseWarmupAsync);
        }
    }

    private async Task SuperviseWarmupAsync()
    {
        try
        {
            // The service lease spans empty slots and instance replacement, avoiding repeated static-package reads at capacity one or while the warm pool is empty.
            // Initial verification runs in the background. Create no instance before it succeeds; after failure, recreate the allocator and recheck deployment.
            if (!validation.Options.UsesReservedRuntime)
                sharedResources = await AfdevSharedResources.AcquireAsync(validation, warmStopping.Token).ConfigureAwait(false);

            while (true)
            {
                lock (gate)
                {
                    if (disposed) return;

                    int missing = validation.Options.WarmInstances - instances.Values.Count(value => !value.Bound && !value.Retiring);

                    // Do not start resource preparation during the short first-map budget; background validation and engine startup must not compete with the current OPEN.
                    if (allocations.Values.Any(value => !value.Ready && !value.Closing && !value.Failed)) missing = 0;

                    for (int count = 0; count < missing; count++)
                    {
                        int slot = Enumerable.Range(0, validation.Options.MaxInstances)
                            .FirstOrDefault(candidate => !instances.ContainsKey(candidate), -1);

                        if (slot < 0) break;

                        var instance = new WarmInstance(slot, new AfdevProcessHost(validation, slot, log));

                        instances.Add(slot, instance);
                        instance.Worker = Task.Run(() => MaintainWarmInstanceAsync(instance));
                    }
                }

                await Task.Delay(500, warmStopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (warmStopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate)
            {
                warmupFailed = true;

                foreach (var instance in instances.Values)
                    instance.Available = false;

                warmStopping.Cancel();
            }

            Report($"AFDEV 共享资源初始化或预热调度失败 [{error.GetType().Name}]：{error.Message}；已停止预热和新开赛，现有实例保留到正常回收。");
        }
    }

    private async Task MaintainWarmInstanceAsync(WarmInstance instance)
    {
        try
        {
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(warmStopping.Token);

            preparation.CancelAfter(TimeSpan.FromSeconds(Math.Min(1800,
                validation.Options.MaxStartupAttempts * (validation.Options.StartupTimeoutSeconds + 120) + 15)));
            await instance.Host.WarmAsync(preparation.Token).ConfigureAwait(false);

            lock (gate)
            {
                if (disposed || warmupFailed) return;

                instance.Available = true;
                warmFailureCount = 0;
            }

            Report($"AFDEV 槽={instance.Slot} 引擎已预热，可以接受新开赛；尚未创建本局地图。");

            while (true)
            {
                await Task.Delay(1000, warmStopping.Token).ConfigureAwait(false);
                await instance.CheckGate.WaitAsync(warmStopping.Token).ConfigureAwait(false);

                try
                {
                    lock (gate) { if (instance.Bound || disposed || warmupFailed) return; }

                    await instance.Host.CheckWarmAsync(warmStopping.Token).ConfigureAwait(false);
                }
                finally { instance.CheckGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (warmStopping.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            lock (gate)
            {
                instance.Available = false;
                // The round owns disposal of a claimed instance, and BindAsync revalidates it. Do not preemptively destroy another operation's host.
                if (instance.Bound) return;

                instance.Retiring = true;
                warmFailureCount++;
            }

            Report($"AFDEV 预热槽={instance.Slot} 失效 [{error.GetType().Name}]，仅回收本实例。");
        }

        while (!warmStopping.IsCancellationRequested)
        {
            try
            {
                await instance.Host.StopAsync().ConfigureAwait(false);

                lock (gate) instances.Remove(instance.Slot);

                return;
            }
            catch (Exception error)
            {
                Report($"AFDEV 预热槽={instance.Slot} 清理待重试 [{error.GetType().Name}]，继续占用容量。");
            }

            try { await Task.Delay(1000, warmStopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (warmStopping.IsCancellationRequested) { return; }
        }
    }

    /// <summary>
    /// Waits for background work to exit. Task failure neither transfers host ownership nor prevents
    /// subsequent StopAsync cleanup.
    /// </summary>
    private async Task WaitForWarmWorkerAsync(WarmInstance instance)
    {
        try { await instance.Worker.ConfigureAwait(false); }
        catch (Exception error)
        {
            Report($"AFDEV 预热槽={instance.Slot} 后台任务异常 [{error.GetType().Name}]：{error.Message}；继续回收同一宿主。");
        }
    }

    // Diagnostic subscribers own no physical resources; failed recording cannot block cleanup or change allocation results.
    private void Report(string message)
    {
        try { log?.Invoke(message); }
        catch (Exception) { }
    }

    private sealed class WarmInstance(int slot, AfdevProcessHost host)
    {
        internal int Slot { get; } = slot;
        internal AfdevProcessHost Host { get; } = host;
        internal SemaphoreSlim CheckGate { get; } = new(1, 1);
        internal Task Worker = Task.CompletedTask;
        internal bool Available;
        internal bool Bound;
        internal bool Retiring;
    }
}
