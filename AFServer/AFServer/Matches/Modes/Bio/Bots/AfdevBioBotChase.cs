namespace AFServer.AssaultFire.Matches.Bots;

internal sealed partial class AfdevBioBotPatrol
{
    private readonly Dictionary<uint, DirectObservation> directObservations = [];
    private readonly Dictionary<uint, DirectObservation> pursuitRejections = [];
    private readonly Dictionary<(uint From, uint To), DateTimeOffset> bridgeRetryAfter = [];
    private readonly HashSet<(uint From, uint To)> confirmedBridges = [];
    private Dictionary<uint, uint[]>? walkGraph;
    private DateTimeOffset graphExpires;
    private float graphRadius;
    private float graphHeight;
    private long chaseRevision = -1;
    private float[]? chaseTargetOrigin;
    private double chaseBestDistance = double.MaxValue;
    private DateTimeOffset chaseProgressAt;
    private int chaseFailures;
    private long directQueries;

    private void ResetChase()
    {
        directObservations.Clear();
        pursuitRejections.Clear();
        bridgeRetryAfter.Clear();
        confirmedBridges.Clear();
        walkGraph = null;
        chaseRevision = -1;
        chaseTargetOrigin = null;
        chaseBestDistance = double.MaxValue;
        chaseFailures = 0;
        learnedRejected.Clear();
        learnedTargetRevision = -1;
        learnedBestCost = double.PositiveInfinity;
    }

    /// <summary>
    /// Strategic targets are independent of current waypoints. For disconnected native graphs, test direct
    /// reach from the actual position before selecting an entrance. Only directed crossings actually
    /// reached enter this Pawn's temporary experience; never fabricate engine links.
    /// </summary>
    private async Task<NavigationPlan?> ChooseChasePlanAsync(PatrolPawn pawn, AfdevBioBotCombatDecision decision,
        CancellationToken token)
    {
        float[] target = decision.TargetLocation!;
        Dictionary<uint, PatrolPoint> navigationPoints = points ?? throw new InvalidOperationException("追击前未读取地图导航点。");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        double distance = Math.Sqrt(DistanceSquared(pawn.Location, target));

        if (chaseRevision != decision.Revision || chaseTargetOrigin == null ||
            DistanceSquared(chaseTargetOrigin, target) >= 768 * 768)
        {
            chaseRevision = decision.Revision;
            chaseTargetOrigin = (float[])target.Clone();
            chaseBestDistance = distance;
            chaseProgressAt = now;
            chaseFailures = 0;
        }

        if (!pursuitRejections.TryGetValue(decision.TargetPawn, out DirectObservation? rejected) || rejected.Expires <= now ||
            DistanceSquared(rejected.Origin, pawn.Location) >= 256 * 256 ||
            DistanceSquared(rejected.GoalLocation, target) >= 256 * 256)
            return new NavigationPlan(decision.TargetPawn, AfdevBioBotNavigationMode.Pursuit,
                "保留敌方 Pawn，每段由原生直达或寻路接近；失败后再探索入口或使用示范。");

        NavigationPlan? learned = await ChooseLearnedPlanAsync(pawn, decision, token).ConfigureAwait(false);

        if (learned != null)
            return learned;

        PatrolPoint? anchor = FindAnchor(pawn);

        if (anchor == null)
            return null;

        Dictionary<uint, uint[]> graph = ReadWalkGraph(pawn);
        Dictionary<uint, PathEntry> paths = FindPaths(anchor.Identity.Address, graph);
        PatrolPoint targetPoint = navigationPoints.GetValueOrDefault(decision.TargetAnchor) is { } nativeTarget &&
            Math.Abs(nativeTarget.Location[2] - target[2]) <= 128 && DistanceSquared(nativeTarget.Location, target) <= 768 * 768
                ? nativeTarget : navigationPoints.Values.MinBy(point => DistanceSquared(point.Location, target))!;

        // Query only a few nearby disconnected nodes. Cache checks by current position and target location, reevaluating after movement.
        PatrolPoint[] outside = navigationPoints.Values.Where(point => !paths.ContainsKey(point.Identity.Address) && CanRetry(point.Identity.Address) &&
                DistanceSquared(point.Location, pawn.Location) <= 1800 * 1800)
            .OrderBy(point => DistanceSquared(point.Location, pawn.Location) + DistanceSquared(point.Location, target) * 0.2)
            .Take(3).ToArray();

        foreach (PatrolPoint point in outside)
        {
            bool? reachable = await DirectlyReachableAsync(pawn, point.Identity.Address, point.Location, token).ConfigureAwait(false);

            if (reachable == null)
                return null;
            if (reachable == true)
                return new NavigationPlan(point.Identity.Address, AfdevBioBotNavigationMode.Direct,
                    "从实际站位可直达另一导航区域。", Near(pawn.Location, anchor.Location) ? anchor.Identity.Address : 0);

            // Inspect only three entrances per cycle, but temporarily deprioritize failures so later cycles can examine the fourth and subsequent candidates.
            retryAfter[point.Identity.Address] = now.AddSeconds(10);
        }

        if (paths.ContainsKey(targetPoint.Identity.Address) && CanRetry(targetPoint.Identity.Address) &&
            DistanceSquared(targetPoint.Location, pawn.Location) >= 128 * 128)
            return PlanAlongPath(pawn, targetPoint.Identity.Address, paths, "沿完整有向路线前往目标区域。");

        if (!paths.ContainsKey(targetPoint.Identity.Address))
        {
            var bridges = paths.Keys.SelectMany(from => navigationPoints.Values.Where(to => !paths.ContainsKey(to.Identity.Address))
                    .Select(to => new ChooseChasePlanAsyncJson1 { From = navigationPoints[from], To = to,
                        Gap = Math.Sqrt(DistanceSquared(navigationPoints[from].Location, to.Location)) }))
                .Where(pair => pair.Gap is >= 96 and <= 1600 &&
                    Math.Abs(pair.From.Location[2] - pair.To.Location[2]) <= 384 &&
                    CanRetry(pair.From.Identity.Address) && CanRetry(pair.To.Identity.Address) &&
                    (!bridgeRetryAfter.TryGetValue((pair.From.Identity.Address, pair.To.Identity.Address), out DateTimeOffset after) || after <= now))
                .OrderBy(pair => paths[pair.From.Identity.Address].Cost + pair.Gap * 1.25 +
                    Math.Sqrt(DistanceSquared(pair.To.Location, target)) * 1.5).ToArray();

            foreach (var bridge in bridges.Take(3))
            {
                uint from = bridge.From.Identity.Address;
                uint to = bridge.To.Identity.Address;

                if (!Near(pawn.Location, bridge.From.Location))
                    return PlanAlongPath(pawn, from, paths, "目标位于断开的导航区域，先前往候选入口。");
                bool? reachable = await DirectlyReachableAsync(pawn, to, bridge.To.Location, token).ConfigureAwait(false);

                if (reachable == null)
                    return null;
                if (reachable == true)
                    return new NavigationPlan(to, AfdevBioBotNavigationMode.Direct, "已到入口，原生确认可直接进入下一片区域。", from);

                bridgeRetryAfter[(from, to)] = now.AddSeconds(20);
                chaseFailures++;
                save("behavior-entry-rejected", new ChooseChasePlanAsyncJson2 { TargetPawn = decision.TargetPawn, From = bridge.From, To = bridge.To,
                    Location = pawn.Location, Reason = "该实际站位的 ActorReachable 返回 false。" });
            }
        }

        // If the nearest node remains unreachable directly, change viewpoint or entrance rather than repeatedly reporting Reached between the two nearest points.
        PatrolPoint? approach = paths.Keys.Select(address => navigationPoints[address])
            .Where(point => DistanceSquared(point.Location, pawn.Location) >= 128 * 128 && CanRetry(point.Identity.Address) &&
                (!noPathOrigins.TryGetValue((anchor.Identity.Address, point.Identity.Address), out float[]? origin) ||
                    DistanceSquared(origin, pawn.Location) >= 256 * 256))
            .OrderBy(point => Math.Sqrt(DistanceSquared(point.Location, target)) + paths[point.Identity.Address].Cost * 0.2 +
                (visits.TryGetValue(point.Identity.Address, out long visit) ? Math.Max(0, 6 - (routes - visit)) * 240 : 0))
            .FirstOrDefault();

        return approach == null ? null : PlanAlongPath(pawn, approach.Identity.Address, paths, "直达受阻，换接近位置并保留战略目标。");
    }

    private async Task<bool?> DirectlyReachableAsync(PatrolPawn pawn, uint goal, float[] goalLocation, CancellationToken token)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (directObservations.TryGetValue(goal, out DirectObservation? previous) && previous.Expires > now &&
            DistanceSquared(previous.Origin, pawn.Location) < 96 * 96 && DistanceSquared(previous.GoalLocation, goalLocation) < 96 * 96)
            return previous.Reachable;

        AfdevBioBotRouteResult result = await runtime.RunNavigationRouteAsync(participant, goal, token,
            AfdevBioBotNavigationMode.DirectReachability).ConfigureAwait(false);

        directQueries++;
        save("behavior-direct-check", new DirectlyReachableAsyncJson1 { directQueries = directQueries, Identity = pawn.Identity, Location = pawn.Location, goal = goal, goalLocation = goalLocation, result = result });

        if (result.RequiresProcessStop)
            throw new InvalidOperationException("BIO 直接可达查询未能安全退休：" + result.Reason);

        if (result.Outcome is not (AfdevBioBotRouteOutcome.DirectlyReachable or AfdevBioBotRouteOutcome.NoPath))
            return null;

        bool reachable = result.Outcome == AfdevBioBotRouteOutcome.DirectlyReachable;

        directObservations[goal] = new DirectObservation((float[])pawn.Location.Clone(), (float[])goalLocation.Clone(),
            reachable, now.AddSeconds(reachable ? 0.5 : 3));

        return reachable;
    }

    private Dictionary<uint, uint[]> ReadWalkGraph(PatrolPawn pawn)
    {
        if (walkGraph == null || DateTimeOffset.UtcNow >= graphExpires || graphRadius != pawn.Radius || graphHeight != pawn.Height)
        {
            walkGraph = points!.Keys.ToDictionary(point => point, point => ReadNeighbors(point, pawn).Where(points.ContainsKey).ToArray());
            graphExpires = DateTimeOffset.UtcNow.AddSeconds(3);
            graphRadius = pawn.Radius;
            graphHeight = pawn.Height;
        }

        return walkGraph;
    }

    private Dictionary<uint, PathEntry> FindPaths(uint start, Dictionary<uint, uint[]> graph)
    {
        var result = new Dictionary<uint, PathEntry> { [start] = new(0, 0, false) };
        var pending = new PriorityQueue<uint, double>();

        pending.Enqueue(start, 0);

        while (pending.TryDequeue(out uint from, out double cost))
        {
            if (cost > result[from].Cost)
                continue;

            IEnumerable<(uint To, bool Direct)> edges = graph[from].Select(to => (to, false))
                .Concat(confirmedBridges.Where(edge => edge.From == from &&
                    (!bridgeRetryAfter.TryGetValue(edge, out DateTimeOffset after) || after <= DateTimeOffset.UtcNow))
                    .Select(edge => (edge.To, true)));

            foreach ((uint to, bool direct) in edges)
            {
                double next = cost + Math.Sqrt(DistanceSquared(points![from].Location, points[to].Location));

                if (result.TryGetValue(to, out PathEntry? previous) && previous.Cost <= next)
                    continue;

                result[to] = new PathEntry(next, from, direct);
                pending.Enqueue(to, next);
            }
        }

        return result;
    }

    private NavigationPlan PlanAlongPath(PatrolPawn pawn, uint goal, Dictionary<uint, PathEntry> paths, string reason)
    {
        var route = new List<(uint From, uint To, bool Direct)>();

        for (uint current = goal; paths[current].Previous != 0; current = paths[current].Previous)
            route.Add((paths[current].Previous, current, paths[current].Direct));

        route.Reverse();

        foreach (var edge in route)
        {
            if (!edge.Direct)
                continue;

            return Near(pawn.Location, points![edge.From].Location)
                ? new NavigationPlan(edge.To, AfdevBioBotNavigationMode.Direct, "复用本 Pawn 实际走通过的跨区段，执行前仍原生检查。", edge.From)
                : new NavigationPlan(edge.From, AfdevBioBotNavigationMode.FindPath, "先沿原生连接到已验证跨区段的入口。");
        }

        return new NavigationPlan(goal, AfdevBioBotNavigationMode.FindPath, reason);
    }

    private void RecordChaseResult(PatrolPawn before, AfdevBioBotCombatDecision? decision, NavigationPlan plan, AfdevBioBotRouteResult result)
    {
        if (decision?.TargetLocation == null)
            return;

        PatrolPawn? after = ReadPawn();

        if (after == null || after.Identity != before.Identity)
            return;

        bool crossed = plan.BridgeFrom != 0 && result.Outcome == AfdevBioBotRouteOutcome.Reached;

        if (plan.Mode == AfdevBioBotNavigationMode.Pursuit &&
            result.Outcome is AfdevBioBotRouteOutcome.NoPath or AfdevBioBotRouteOutcome.Stalled or AfdevBioBotRouteOutcome.TimedOut)
            pursuitRejections[plan.Goal] = new DirectObservation((float[])after.Location.Clone(),
                (float[])decision.TargetLocation.Clone(), false, DateTimeOffset.UtcNow.AddSeconds(3));

        if (plan.Mode == AfdevBioBotNavigationMode.Direct &&
            result.Outcome is AfdevBioBotRouteOutcome.NoPath or AfdevBioBotRouteOutcome.Stalled or AfdevBioBotRouteOutcome.TimedOut)
        {
            directObservations.Remove(plan.Goal);
            retryAfter[plan.Goal] = DateTimeOffset.UtcNow.AddSeconds(3);
        }

        if (crossed)
        {
            confirmedBridges.Add((plan.BridgeFrom, plan.Goal));
            save("behavior-confirmed-bridge", new RecordChaseResultJson1 { Identity = before.Identity, From = points![plan.BridgeFrom], To = points[plan.Goal],
                Location = after.Location, RouteId = result.RouteId, Limitation = "只属于当前世界和 Pawn，未持久化跨进程指针。" });
        }
        else if (plan.BridgeFrom != 0 && result.Outcome is AfdevBioBotRouteOutcome.NoPath or AfdevBioBotRouteOutcome.Stalled or AfdevBioBotRouteOutcome.TimedOut)
        {
            confirmedBridges.Remove((plan.BridgeFrom, plan.Goal));
            bridgeRetryAfter[(plan.BridgeFrom, plan.Goal)] = DateTimeOffset.UtcNow.AddSeconds(20);
        }

        double remaining = Math.Sqrt(DistanceSquared(after.Location, decision.TargetLocation));
        bool progressed = crossed || RecordLearnedResult(before, plan, result) || remaining < chaseBestDistance - 96;

        if (progressed)
        {
            chaseBestDistance = remaining;
            chaseProgressAt = DateTimeOffset.UtcNow;
            chaseFailures = 0;
        }
        else if (result.Outcome is AfdevBioBotRouteOutcome.NoPath or AfdevBioBotRouteOutcome.Stalled or AfdevBioBotRouteOutcome.TimedOut
            or AfdevBioBotRouteOutcome.Reached)
            chaseFailures++;

        save("behavior-progress", new RecordChaseResultJson2 { TargetPawn = decision.TargetPawn, routes = routes, plan = plan, Outcome = result.Outcome, Location = before.Location,
            After = after.Location, RemainingStraightDistance = remaining, chaseBestDistance = chaseBestDistance, progressed = progressed,
            chaseProgressAt = chaseProgressAt, chaseFailures = chaseFailures, ConfirmedBridges = confirmedBridges.Count, directQueries = directQueries,
            GoalIsEnemy = plan.Goal == decision.TargetPawn });

        if (chaseFailures >= 3 && DateTimeOffset.UtcNow - chaseProgressAt >= TimeSpan.FromSeconds(30))
            engagement?.DeferTarget(decision.TargetPawn, "多个入口或接近动作未取得进展，短暂降低该目标优先级。");
    }

    private bool CanRetry(uint goal) => !retryAfter.TryGetValue(goal, out DateTimeOffset after) || after <= DateTimeOffset.UtcNow;

    private void RecordChaseWait(AfdevBioBotCombatDecision? decision)
    {
        if (decision?.TargetLocation == null || chaseRevision != decision.Revision)
            return;

        chaseFailures++;

        if (chaseFailures >= 3 && DateTimeOffset.UtcNow - chaseProgressAt >= TimeSpan.FromSeconds(30))
            engagement?.DeferTarget(decision.TargetPawn, "持续没有可执行路线，暂时换目标并保留入口探索。");
    }

    private static bool Near(float[] left, float[] right) => Math.Abs(left[2] - right[2]) <= 96 && DistanceSquared(left, right) <= 160 * 160;

    private sealed record NavigationPlan(uint Goal, AfdevBioBotNavigationMode Mode, string Reason, uint BridgeFrom = 0,
        AfdevBioBotPoint? Point = null, int LearnedNode = -1, double LearnedCost = 0, AfdevBioBotWalkStep? WalkStep = null,
        bool Evading = false);
    private sealed record PathEntry(double Cost, uint Previous, bool Direct);
    private sealed record DirectObservation(float[] Origin, float[] GoalLocation, bool Reachable, DateTimeOffset Expires);
}
