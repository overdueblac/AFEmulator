using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevMatchAllocator
{
    /// <summary>
    /// Prefers this match's latest statistics. During native-object removal, uses a retained read-only
    /// sample from the same allocation, never the next match.
    /// </summary>
    public async ValueTask<MatchSettlementSnapshot> ReadSettlementAsync(MatchStartSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Allocation allocation;

        lock (gate)
        {
            if (!allocations.TryGetValue(snapshot.RoomId, out allocation!) ||
                !ReferenceEquals(allocation.Snapshot, snapshot) || !allocation.Ready || allocation.Closing || allocation.Failed)
                throw new InvalidOperationException("当前分配已经不可读取结算。");
        }

        await CaptureSettlementAsync(allocation, cancellationToken).ConfigureAwait(false);

        return Volatile.Read(ref allocation.Settlement) ??
            throw new InvalidOperationException("本局尚未取得有效原生结算统计。");
    }

    /// <summary>
    /// Retains a human player's final sample before exact-channel revocation, including when UDP closes
    /// before A117 arrives.
    /// </summary>
    public async Task PreserveLeavingSettlementAsync(Guid allocationId, Guid sessionId)
    {
        Allocation? allocation;

        lock (gate)
        {
            allocation = allocations.Values.SingleOrDefault(value => value.Snapshot.AllocationId == allocationId);

            if (allocation is null || !allocation.Ready || allocation.Closing || allocation.Failed ||
                !allocation.Snapshot.Members.Any(member => member.Session.SessionId == sessionId))
                return;
        }

        await CaptureSettlementAsync(allocation, CancellationToken.None, sessionId).ConfigureAwait(false);
    }

    private async Task CaptureSettlementAsync(Allocation allocation, CancellationToken cancellationToken, Guid? departingSessionId = null)
    {
        if (Volatile.Read(ref allocation.Settlement)?.Completed == true) return;

        try
        {
            var snapshot = await allocation.Host.ReadSettlementAsync(allocation.Snapshot, cancellationToken,
                departingSessionId).ConfigureAwait(false);

            lock (gate)
            {
                if (allocation.Settlement is null || allocation.Settlement.ObservedAtUtc <= snapshot.ObservedAtUtc)
                    Volatile.Write(ref allocation.Settlement, snapshot);

                allocation.SettlementFailure = null;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // PRI removal may race with read-only sampling. Keep the previous sample without terminating a healthy world or fabricating zero-score successful settlement.
            string failure = error.GetType().Name + ": " + error.Message;

            if (allocation.SettlementFailure != failure)
                Report($"SETTLEMENT_CAPTURE_RETRY allocation={allocation.Snapshot.AllocationId:N} {failure}");

            allocation.SettlementFailure = failure;
        }
    }
}
