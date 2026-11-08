using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Service-level engine warmup and exclusive round allocation. Start clicks consume only warmed instances;
/// failed cleanup retains the instance and slot.
/// </summary>
public sealed partial class AfdevMatchAllocator(AfdevRuntimeValidation validation, string publicAddress,
    Action<string>? log = null) : IMatchSnapshotAllocator, IMatchPreparationAllocator, IMatchServerHealthMonitor,
    IMatchSettlementAllocator, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<ulong, Allocation> allocations = [];
    private bool disposed;

    /// <summary>
    /// Authoritative coin-deduction entry point bound by the composition root. Native processes must not
    /// hold MongoDB access or modify player saves directly.
    /// </summary>
    public IPveReviveCoinStore? ReviveCoinStore { get; set; }

    /// <summary>
    /// Notifies the lobby to revoke eligibility after readiness loss. Invokes events outside the allocation
    /// lock and never disposes a Job with live grants directly.
    /// </summary>
    public event Action<Guid>? AllocationFailed;

    /// <summary>
    /// Deadline for actual world readiness and endpoint publication. Background warmup and separate bot
    /// preparation do not consume the human start budget.
    /// </summary>
    public TimeSpan PreparationTimeout => TimeSpan.FromSeconds(validation.Options.StartupTimeoutSeconds + 5);

    /// <summary>
    /// Uses the same world deadline after checking the frozen roster. Resource-preparation failure is
    /// revoked by the original allocation without blocking human loading.
    /// </summary>
    public TimeSpan GetPreparationTimeout(MatchStartSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateBotPlan(snapshot);

        return PreparationTimeout;
    }

    /// <summary>
    /// Reads only the current allocation's host stage, granting neither readiness nor eligibility.
    /// </summary>
    public (AfdevRuntimeState State, int Attempt, int Maximum) GetPreparation(Guid allocationId)
    {
        lock (gate)
        {
            var entry = allocations.Values.SingleOrDefault(value => value.Snapshot.AllocationId == allocationId);

            return (entry?.Host.State ?? AfdevRuntimeState.Created, entry?.Host.AttemptCount ?? 0,
                validation.Options.MaxStartupAttempts);
        }
    }

    public ValueTask<MatchEndpoint> ReserveAsync(GameRoom room, CancellationToken cancellationToken)
        => ValueTask.FromException<MatchEndpoint>(new InvalidOperationException("AFDEV 必须使用大厅已冻结的唯一开赛快照。"));

    public async ValueTask<MatchEndpoint> ReserveAsync(MatchStartSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBotPlan(snapshot);

        if (snapshot.AllocationId == Guid.Empty || snapshot.RoomId == 0)
            throw new InvalidOperationException("AFDEV 开赛必须保留真实 allocation 与房间身份。");

        // Confirm that this match's map belongs to the same verified resource manifest before claiming a warm instance; do not substitute the warmup map.
        validation.SelectRoute(snapshot.Descriptor.ModeId, snapshot.Descriptor.MapId);

        Allocation allocation;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (warmupFailed)
                throw new InvalidOperationException("AFDEV 共享资源初始化或预热调度已失败，当前不能接受新开赛；请核查部署诊断。");

            if (allocations.ContainsKey(snapshot.RoomId))
                throw new InvalidOperationException("房间已有启动或尚未完整回收的 AFDEV allocation。");

            if (allocations.Values.Any(value => value.Snapshot.AllocationId == snapshot.AllocationId))
                throw new InvalidOperationException("AFDEV allocation 不能重复绑定。");

            var instance = instances.Values.FirstOrDefault(value => value.Available);

            if (instance is null)
                throw new InvalidOperationException("AFDEV 当前没有可用预热实例；本次开赛已拒绝，请准备完成后重新开始。");

            instance.Available = false;
            instance.Bound = true;
            allocation = new Allocation(snapshot, instance);
            allocations.Add(snapshot.RoomId, allocation);
        }

        try
        {
            // Serialize idle verification with the first map open. Background work stops checking the frontend world once an instance is claimed.
            await allocation.Instance.CheckGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            AfdevNativeEvidence evidence;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                evidence = await allocation.Host.BindWithReviveCoinsAsync(snapshot, ReviveCoinStore, cancellationToken).ConfigureAwait(false);
            }
            finally { allocation.Instance.CheckGate.Release(); }

            cancellationToken.ThrowIfCancellationRequested();

            lock (gate)
            {
                if (disposed || allocation.Closing || allocation.Failed || evidence.AllocationId != snapshot.AllocationId)
                    throw new OperationCanceledException("AFDEV 已在端点发布前取消。");

                allocation.Ready = true;
                allocation.Monitor = Task.Run(() => ObserveAsync(allocation));
            }

            // The host reservation exclusively retains the public socket as a port/slot placeholder; it never receives or forwards player UDP.
            // Real player data reaches the loopback upstream only through CONNECT frames redeemed for the current session.
            return new MatchEndpoint(publicAddress, checked((ushort)evidence.PublicPort), new byte[16]);
        }
        catch
        {
            lock (gate) allocation.Failed = true;

            // The host cleans up failed attempts itself. Retain the object for the lobby's same release entry point so cleanup failures remain retryable.
            throw;
        }
    }

    /// <summary>
    /// Retrieves the host through the allocation fixed by the control plane. Datagrams cannot locate an
    /// instance through a client-reported ID.
    /// </summary>
    public AfdevProcessHost GetReadyHost(Guid allocationId)
    {
        lock (gate)
        {
            var entry = allocations.Values.SingleOrDefault(entry => entry.Snapshot.AllocationId == allocationId);

            if (entry is null || !entry.Ready || entry.Closing || entry.Failed || entry.Host.ReadyEvidence is null)
                throw new InvalidOperationException("AFDEV allocation 当前不可用于准入。");

            return entry.Host;
        }
    }

    public MatchStartSnapshot GetSnapshot(Guid allocationId)
    {
        lock (gate)
            return allocations.Values.Single(entry => entry.Snapshot.AllocationId == allocationId).Snapshot;
    }

    public ValueTask<bool> HasFailedAsync(ulong roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
            return ValueTask.FromResult(!allocations.TryGetValue(roomId, out var entry) || entry.Failed || entry.Closing);
    }

    private async Task ObserveAsync(Allocation allocation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, allocation.Stopping.Token).ConfigureAwait(false);
                await allocation.Host.CheckReadyAsync(allocation.Stopping.Token).ConfigureAwait(false);
                await CaptureSettlementAsync(allocation, allocation.Stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (allocation.Stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate) allocation.Failed = true;
            Report($"AFDEV allocation={allocation.Snapshot.AllocationId:N} 健康失效：{error.GetType().Name}。");

            try { AllocationFailed?.Invoke(allocation.Snapshot.AllocationId); }
            catch (Exception notification) { Report($"AFDEV 健康通知失败：{notification.GetType().Name}。"); }
        }
    }

    public async ValueTask ReleaseAsync(ulong roomId, CancellationToken cancellationToken)
    {
        Allocation? allocation;

        lock (gate)
        {
            if (!allocations.TryGetValue(roomId, out allocation)) return;

            allocation.Closing = true;
            allocation.Stopping.Cancel();
        }

        await allocation.CloseGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (allocation.Monitor is not null) await allocation.Monitor.ConfigureAwait(false);
            await WaitForWarmWorkerAsync(allocation.Instance).ConfigureAwait(false);
            await allocation.Host.StopAsync().ConfigureAwait(false);

            lock (gate)
            {
                if (ReferenceEquals(allocations.GetValueOrDefault(roomId), allocation))
                {
                    allocations.Remove(roomId);
                    instances.Remove(allocation.Instance.Slot);
                }
            }
        }
        finally { allocation.CloseGate.Release(); }
    }

    /// <summary>
    /// The composition root must stop the lobby and revoke grants first. Retains objects on disposal
    /// failure so DisposeAsync can be retried.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ulong[] rooms;
        WarmInstance[] owned;

        lock (gate)
        {
            disposed = true;
            warmStopping.Cancel();
        }

        try { await supervisor.ConfigureAwait(false); }
        catch (Exception error)
        {
            Report($"AFDEV 预热监督任务异常 [{error.GetType().Name}]：{error.Message}；继续回收全部自有实例。");
        }

        lock (gate)
        {
            rooms = allocations.Keys.ToArray();
            owned = instances.Values.ToArray();
        }

        await Task.WhenAll(owned.Select(WaitForWarmWorkerAsync)).ConfigureAwait(false);

        List<Exception> failures = [];

        foreach (ulong roomId in rooms)
        {
            try { await ReleaseAsync(roomId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }

        foreach (var instance in owned.Where(instance => !instance.Bound))
        {
            try
            {
                await instance.Host.StopAsync().ConfigureAwait(false);

                lock (gate) instances.Remove(instance.Slot);
            }
            catch (Exception error) { failures.Add(error); }
        }

        if (failures.Count != 0) throw new AggregateException("AFDEV 仍有实例待清理。", failures);

        // Release service-level protection only after every physical host and workspace is reclaimed. Concurrent or repeated shutdown transfers ownership once.
        Interlocked.Exchange(ref sharedResources, null)?.Dispose();
    }

    private sealed class Allocation(MatchStartSnapshot snapshot, WarmInstance instance)
    {
        internal MatchStartSnapshot Snapshot { get; } = snapshot;
        internal WarmInstance Instance { get; } = instance;
        internal AfdevProcessHost Host => Instance.Host;
        internal HashSet<Guid> RemainingFighterSessions { get; } =
            [.. snapshot.Members.Where(member => !member.Observer).Select(member => member.Session.SessionId)];
        internal bool Ready;
        internal bool Closing;
        internal bool Failed;
        internal CancellationTokenSource Stopping { get; } = new();
        internal SemaphoreSlim CloseGate { get; } = new(1, 1);
        internal Task? Monitor;
        internal MatchSettlementSnapshot? Settlement;
        internal string? SettlementFailure;
    }
}
