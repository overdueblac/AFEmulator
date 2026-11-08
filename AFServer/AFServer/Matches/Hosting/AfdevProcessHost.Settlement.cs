using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevProcessHost
{
    private AfdevSettlementReader? settlementReader;

    /// <summary>
    /// Samples this match's statistics under the host operation gate. Reads memory only, never invoking
    /// EndGame or changing native scores or outcomes.
    /// </summary>
    /// <param name="departingSessionId">
    /// Optional exact departing identity, frozen after sampling so a lingering PRI cannot increase its
    /// statistics.
    /// </param>
    public async Task<MatchSettlementSnapshot> ReadSettlementAsync(MatchStartSnapshot snapshot,
        CancellationToken cancellationToken = default, Guid? departingSessionId = null)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (State != AfdevRuntimeState.SessionReady || ReadyEvidence is null || process is null ||
                Binding is null || Binding.AllocationId != snapshot.AllocationId || Binding.RoomId != snapshot.RoomId ||
                Binding.ModeId != snapshot.Descriptor.ModeId || Binding.MapId != snapshot.Descriptor.MapId)
                throw new InvalidOperationException("结算不属于当前严格就绪的原生世界。");

            cancellationToken.ThrowIfCancellationRequested();
            settlementReader ??= snapshot.Descriptor.ModeId switch
            {
                0x1001 or 0x1002 or 0x1004 => new AfdevPvpSettlementReader(process, snapshot, ReadyEvidence),
                0x0204 => new AfdevBioSettlementReader(process, snapshot, ReadyEvidence),
                0x2001 => new AfdevSurvivalSettlementReader(process, snapshot, ReadyEvidence),
                0x2002 => new AfdevDefenseSettlementReader(process, snapshot, ReadyEvidence),
                0x2003 => new AfdevEncirclementSettlementReader(process, snapshot, ReadyEvidence),
                0x2005 => new AfdevTowerDefenseSettlementReader(process, snapshot, ReadyEvidence),
                _ => throw new InvalidOperationException("当前玩法没有已接入的原生结算读取器。")
            };

            var result = settlementReader.Capture(ReadyEvidence);

            if (departingSessionId is { } departing) settlementReader.Retire(departing);

            return result;
        }
        finally { operationGate.Release(); }
    }
}
