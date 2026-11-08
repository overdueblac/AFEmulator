namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevMatchAllocator
{
    /// <summary>
    /// Removes exact participating sessions individually. Stops global bots only when the final human
    /// exits; slow-loading slots and observers are not interchangeable.
    /// </summary>
    public async Task StopBotWorkAsync(Guid allocationId, Guid sessionId)
    {
        AfdevProcessHost? host;

        lock (gate)
        {
            var allocation = allocations.Values.SingleOrDefault(value => value.Snapshot.AllocationId == allocationId);

            if (allocation?.Snapshot.BioBotPlan is not { Bots.Length: > 0 } plan ||
                !plan.Humans.Any(human => !human.Observer && human.SessionId == sessionId))
                return;

            allocation.RemainingFighterSessions.Remove(sessionId);
            if (allocation.RemainingFighterSessions.Count != 0) return;

            // Repeated revocation joins the same stop task even after the set clears, preserving retries. The original allocation still owns whole-match Job cleanup.
            host = allocation.Host;
        }

        await host.StopBotWorkAsync().ConfigureAwait(false);
    }
}
