namespace AFServer.AssaultFire.Matches.Bots;

internal sealed partial class AfdevBioBotPatrol
{
    private readonly Dictionary<int, LearnedRejection> learnedRejected = [];
    private int learnedRevision;
    private long learnedTargetRevision = -1;
    private double learnedBestCost = double.PositiveInfinity;
    private long learnedActions;
    private long learnedObservedTrials;
    private long learnedObservedReached;
    private readonly Dictionary<AfdevBioBotRouteOutcome, long> learnedOutcomes = [];

    private async Task<NavigationPlan?> ChooseLearnedPlanAsync(PatrolPawn pawn, AfdevBioBotCombatDecision decision,
        CancellationToken token)
    {
        if (learnedRoutes == null || decision.TargetLocation == null)
            return null;

        AfdevBioBotWalkGraph graph = learnedRoutes.Snapshot;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (learnedRevision != graph.Revision || learnedTargetRevision != decision.Revision)
        {
            learnedRevision = graph.Revision;
            learnedTargetRevision = decision.Revision;
            learnedRejected.Clear();
            learnedBestCost = double.PositiveInfinity;
        }

        var origin = new AfdevBioBotPoint(pawn.Location[0], pawn.Location[1], pawn.Location[2]);
        var target = new AfdevBioBotPoint(decision.TargetLocation[0], decision.TargetLocation[1], decision.TargetLocation[2]);
        HashSet<int> excluded = learnedRejected.Where(item => item.Value.Until > now &&
                AfdevBioBotWalkRoutes.DistanceSquared(item.Value.Origin, origin) <= 128 * 128)
            .Select(item => item.Key).ToHashSet();
        IReadOnlyList<AfdevBioBotWalkCandidate> candidates = graph.Recommend(origin, pawn.Height, target, excluded);

        foreach (AfdevBioBotWalkCandidate candidate in candidates.Take(3))
        {
            AfdevBioBotWalkStep? step = candidate.From is { } from && AfdevBioBotWalkStep.CanStartAt(from, candidate.Point, origin)
                ? new AfdevBioBotWalkStep(from, candidate.Point, pawn.Identity) : null;

            if (step != null)
            {
                // Near actual adjacent segments, the sole action executor performs reachability queries and necessary trial walking. Planning does not repeat the same query.
                save("learned-route-candidate", new ChooseLearnedPlanAsyncJson1 { Revision = graph.Revision, Identity = pawn.Identity, origin = origin, candidate = candidate,
                    NativeReachabilityPending = true });
                return new NavigationPlan(0, AfdevBioBotNavigationMode.Point, "沿身边的连续行走示范接近敌人，执行时原生预判并核对到达。",
                    Point: candidate.Point, LearnedNode: candidate.Node, LearnedCost: candidate.TotalCost, WalkStep: step);
            }

            AfdevBioBotRouteResult query = await runtime.RunNavigationRouteAsync(participant, candidate.Point,
                token, queryOnly: true).ConfigureAwait(false);

            save("learned-route-query", new ChooseLearnedPlanAsyncJson2 { Revision = graph.Revision, Identity = pawn.Identity, Radius = pawn.Radius, Height = pawn.Height, origin = origin,
                TargetPawn = decision.TargetPawn, candidate = candidate, Outcome = query.Outcome });

            if (query.RequiresProcessStop)
                throw new InvalidOperationException("示范坐标的原生可达查询未安全退休：" + query.Reason);

            if (query.Outcome == AfdevBioBotRouteOutcome.DirectlyReachable)
                return new NavigationPlan(0, AfdevBioBotNavigationMode.Point, "沿玩家连续行走示范接近敌人；当前段已通过原生可达检查。",
                    Point: candidate.Point, LearnedNode: candidate.Node, LearnedCost: candidate.TotalCost);
            if (query.Outcome != AfdevBioBotRouteOutcome.NoPath)
                return null;

            learnedRejected[candidate.Node] = new LearnedRejection(origin, now.AddSeconds(10));
        }

        return null;
    }

    private bool RecordLearnedResult(PatrolPawn before, NavigationPlan plan, AfdevBioBotRouteResult result)
    {
        if (plan.Point == null)
            return false;

        if (result.Outcome is AfdevBioBotRouteOutcome.NoPath or AfdevBioBotRouteOutcome.Stalled or AfdevBioBotRouteOutcome.TimedOut
            or AfdevBioBotRouteOutcome.BudgetExhausted)
            learnedRejected[plan.LearnedNode] = new LearnedRejection(
                new AfdevBioBotPoint(before.Location[0], before.Location[1], before.Location[2]), DateTimeOffset.UtcNow.AddSeconds(12));

        bool progressed = result.Outcome == AfdevBioBotRouteOutcome.Reached && plan.LearnedCost < learnedBestCost - 32;

        if (progressed)
            learnedBestCost = plan.LearnedCost;

        learnedActions++;
        learnedOutcomes[result.Outcome] = learnedOutcomes.GetValueOrDefault(result.Outcome) + 1;

        if (result.UsedObservedStep)
        {
            learnedObservedTrials++;

            if (result.Outcome == AfdevBioBotRouteOutcome.Reached)
                learnedObservedReached++;
        }

        var evidence = new RecordLearnedResultJson1 { Point = plan.Point, LearnedNode = plan.LearnedNode, LearnedCost = plan.LearnedCost, WalkStep = plan.WalkStep, Radius = before.Radius, Height = before.Height,
            Identity = before.Identity, Outcome = result.Outcome, RouteId = result.RouteId, UsedObservedStep = result.UsedObservedStep,
            NativeReached = result.Outcome == AfdevBioBotRouteOutcome.Reached,
            progressed = progressed, BestCost = double.IsFinite(learnedBestCost) ? learnedBestCost : (double?)null };

        save("learned-route-result", evidence);
        save("learned-route-recent-" + learnedActions % 8, evidence);
        save("learned-route-counts", new RecordLearnedResultJson2 { Actions = learnedActions, Outcomes = learnedOutcomes,
            ObservedTrials = learnedObservedTrials, ObservedReached = learnedObservedReached });
        return progressed;
    }

    private sealed record LearnedRejection(AfdevBioBotPoint Origin, DateTimeOffset Until);
}
