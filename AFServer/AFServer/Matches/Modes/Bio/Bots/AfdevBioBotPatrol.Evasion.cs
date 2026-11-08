namespace AFServer.AssaultFire.Matches.Bots;

internal sealed partial class AfdevBioBotPatrol
{
    private readonly List<EvasionRejection> evasionRejected = [];
    private long evasionAttempts;
    private long evasionMoved;

    private async Task<NavigationPlan?> ChooseEvasionPlanAsync(PatrolPawn pawn, AfdevBioBotCombatDecision decision,
        CancellationToken token)
    {
        AfdevBioBotPoint origin = ToPoint(pawn.Location);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int side = (participant.BotId.GetHashCode() & 1) == 0 ? 1 : -1;

        evasionRejected.RemoveAll(item => item.Until <= now ||
            AfdevBioBotEvasion.HorizontalDistanceSquared(item.Origin, origin) > 96 * 96);

        IEnumerable<AfdevBioBotPoint> candidates = AfdevBioBotEvasion.Recommend(origin, decision.ThreatLocations, side)
            .Concat(points!.Values.Select(item => ToPoint(item.Location))
                .Where(point => AfdevBioBotEvasion.HorizontalDistanceSquared(origin, point) <= 700 * 700 &&
                    AfdevBioBotEvasion.IsSafeDirection(origin, point, decision.ThreatLocations))
                .OrderByDescending(point => AfdevBioBotEvasion.Separation(point, decision.ThreatLocations)))
            .Where(point => !evasionRejected.Any(item =>
                AfdevBioBotEvasion.HorizontalDistanceSquared(item.Point, point) <= 64 * 64));

        foreach (AfdevBioBotPoint point in candidates.Take(4).ToArray())
        {
            AfdevBioBotRouteResult query = await runtime.RunNavigationRouteAsync(participant, point,
                token, queryOnly: true).ConfigureAwait(false);

            save("evasion-query", new ChooseEvasionPlanAsyncJson1 { Identity = pawn.Identity, origin = origin, point = point, Outcome = query.Outcome, ThreatLocations = decision.ThreatLocations });

            if (query.RequiresProcessStop)
                throw new InvalidOperationException("避让可达查询未安全退休：" + query.Reason);
            if (ReadPawn()?.Identity != pawn.Identity)
                return null;
            if (query.Outcome == AfdevBioBotRouteOutcome.DirectlyReachable)
                return new NavigationPlan(0, AfdevBioBotNavigationMode.Point,
                    "普通人类向远离近身敌人的可达位置短步避让，再重新选择交战行为。", Point: point, Evading: true);
            if (query.Outcome != AfdevBioBotRouteOutcome.NoPath)
                return null;

            RejectEvasion(origin, point);
        }

        return null;
    }

    private void RecordEvasionResult(PatrolPawn before, AfdevBioBotCombatDecision decision,
        NavigationPlan plan, AfdevBioBotRouteResult result)
    {
        AfdevBioBotPoint origin = ToPoint(before.Location);
        PatrolPawn? after = ReadPawn();
        bool moved = after != null && after.Identity == before.Identity &&
            AfdevBioBotEvasion.HorizontalDistanceSquared(origin, ToPoint(after.Location)) >= 16 * 16;

        evasionAttempts++;

        if (moved)
            evasionMoved++;
        if (plan.Point is { } point && result.Outcome is AfdevBioBotRouteOutcome.NoPath or
            AfdevBioBotRouteOutcome.Stalled or AfdevBioBotRouteOutcome.TimedOut)
            RejectEvasion(origin, point);

        save("evasion-result-" + evasionAttempts % 8, new RecordEvasionResultJson1 { evasionAttempts = evasionAttempts, evasionMoved = evasionMoved, Identity = before.Identity,
            Before = origin, After = after?.Location, moved = moved, ThreatLocations = decision.ThreatLocations, plan = plan, result = result,
            Limitation = "位移与原生到达分别记录；不能据此断言已躲过某次攻击。" });
    }

    private void RejectEvasion(AfdevBioBotPoint origin, AfdevBioBotPoint point)
    {
        evasionRejected.Add(new EvasionRejection(origin, point, DateTimeOffset.UtcNow.AddSeconds(3)));

        if (evasionRejected.Count > 24)
            evasionRejected.RemoveAt(0);
    }

    private static AfdevBioBotPoint ToPoint(float[] location) => new(location[0], location[1], location[2]);

    private sealed record EvasionRejection(AfdevBioBotPoint Origin, AfdevBioBotPoint Point, DateTimeOffset Until);
}
