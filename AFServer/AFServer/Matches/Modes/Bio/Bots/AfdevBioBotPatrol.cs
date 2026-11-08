using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Continuous movement scheduling for one bot. Reads map navigation points and native links to choose
/// patrol/pursuit targets; the engine owns pathfinding, obstacle avoidance, and motion. One task
/// coordinates combat samples/routes. Before stationary fire or Pawn/team/round changes, cancel and await
/// retirement of the old route.
/// </summary>
internal sealed partial class AfdevBioBotPatrol(AfdevBioBotRuntime runtime, AfdevOwnedProcess process,
    AfdevNativeEvidence world, AfdevBioBotPreparation participant, Action<string, object> save, Action checkBinding,
    AfdevBioBotEngagement? engagement = null, AfdevBioBotWalkRoutes? learnedRoutes = null)
{
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly Dictionary<(uint Class, string Path), uint> fields = [];
    private readonly Dictionary<uint, long> visits = [];
    private readonly Dictionary<uint, DateTimeOffset> retryAfter = [];
    private readonly Dictionary<(uint Anchor, uint Goal), float[]> noPathOrigins = [];
    private Dictionary<uint, PatrolPoint>? points;
    private QueryObject? lastPawn;
    private long routes;
    private int completedSegments;

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                checkBinding();

                AfdevBioBotCombatDecision? decision = engagement == null
                    ? null : await engagement.UpdateAsync(token).ConfigureAwait(false);
                PatrolPawn? pawn = ReadPawn();

                if (pawn == null)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    continue;
                }

                if (decision?.HoldPosition == true)
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                    continue;
                }

                if (lastPawn != pawn.Identity)
                {
                    lastPawn = pawn.Identity;
                    visits.Clear();
                    retryAfter.Clear();
                    noPathOrigins.Clear();
                    evasionRejected.Clear();
                    ResetChase();
                    save("patrol-pawn", new RunAsyncJson1 { Controller = participant.Controller, Pawn = lastPawn, Location = pawn.Location,
                        InstanceId = participant.InstanceId, AttemptId = participant.AttemptId, AllocationId = participant.AllocationId, WorldGeneration = participant.WorldGeneration });
                }

                points ??= ReadPoints();

                NavigationPlan? plan = decision?.Evading == true
                    ? await ChooseEvasionPlanAsync(pawn, decision, token).ConfigureAwait(false)
                    : decision?.TargetLocation != null
                        ? await ChooseChasePlanAsync(pawn, decision, token).ConfigureAwait(false)
                    : ChooseGoal(pawn) is { } patrolGoal
                        ? new NavigationPlan(patrolGoal.Identity.Address, AfdevBioBotNavigationMode.FindPath, "没有有效敌人，沿地图连接巡逻。") : null;

                if (plan == null)
                {
                    if (decision?.Evading != true)
                        RecordChaseWait(decision);
                    save("behavior-wait", new RunAsyncJson2 { decision = decision, Identity = pawn.Identity, Location = pawn.Location, Reason = "当前没有可执行的导航目标，等待原生状态或入口冷却。" });
                    await Task.Delay(500, token).ConfigureAwait(false);
                    continue;
                }

                if (plan.Point is { } destination && engagement != null &&
                    !await engagement.PrepareDestinationAsync(pawn.Identity, destination, token).ConfigureAwait(false))
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                routes++;

                if (plan.Point == null)
                    visits[plan.Goal] = routes;
                save("behavior-plan", new RunAsyncJson3 { routes = routes, decision = decision, Identity = pawn.Identity, Location = pawn.Location, plan = plan });

                AfdevBioBotRouteResult result = await RunRouteAsync(plan, decision, token).ConfigureAwait(false);

                completedSegments += result.Steps.Count(step => step.Completion == AfdevBioBotNavigationPhase.Completed);
                save("patrol-route-" + routes % 8, new RunAsyncJson4 { routes = routes, completedSegments = completedSegments, Pawn = pawn.Identity,
                    plan = plan, decision = decision, result = result });

                if (result.RequiresProcessStop)
                    throw new InvalidOperationException("BIO 巡逻需要回收原 Job：" + result.Reason);

                if (plan.Evading)
                    RecordEvasionResult(pawn, decision!, plan, result);
                else
                    RecordChaseResult(pawn, decision, plan, result);

                if (result.Outcome == AfdevBioBotRouteOutcome.NoPath && plan.Mode == AfdevBioBotNavigationMode.FindPath &&
                    points.TryGetValue(plan.Goal, out PatrolPoint? noPathGoal))
                    RememberNoPath(pawn, noPathGoal);
                if (plan.Point == null && result.Outcome is (AfdevBioBotRouteOutcome.Stalled
                    or AfdevBioBotRouteOutcome.TimedOut or AfdevBioBotRouteOutcome.TargetInvalid))
                    retryAfter[plan.Goal] = DateTimeOffset.UtcNow.AddSeconds(10);

                if (result.Outcome is not (AfdevBioBotRouteOutcome.Reached or AfdevBioBotRouteOutcome.Cancelled))
                    await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (engagement != null)
                await engagement.StopAsync().ConfigureAwait(false);

            AfdevBioBotNavigationState retired = await runtime.StopNavigationAsync(participant, CancellationToken.None).ConfigureAwait(false);

            save("patrol-stopped", new RunAsyncJson5 { routes = routes, completedSegments = completedSegments, retired = retired });

            if (retired.RequiresProcessStop)
                throw new InvalidOperationException("BIO 巡逻未确认安全退休，必须回收原 Job。");
        }
    }

    private async Task<AfdevBioBotRouteResult> RunRouteAsync(NavigationPlan plan, AfdevBioBotCombatDecision? initial,
        CancellationToken token)
    {
        Task<AfdevBioBotRouteResult> StartRoute(CancellationToken running) => plan.WalkStep is { } step
            ? runtime.RunLearnedWalkAsync(participant, step, running)
            : plan.Point is { } point
                ? runtime.RunNavigationRouteAsync(participant, point, running)
                : runtime.RunNavigationRouteAsync(participant, plan.Goal, running, plan.Mode);

        if (engagement == null || initial == null)
            return await StartRoute(token).ConfigureAwait(false);

        using var routeStopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<AfdevBioBotRouteResult> running = StartRoute(routeStopping.Token);

        try
        {
            while (!running.IsCompleted)
            {
                await Task.WhenAny(running, Task.Delay(200, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (running.IsCompleted)
                    break;

                AfdevBioBotCombatDecision current = await engagement.UpdateAsync(token, navigationActive: true,
                    destination: plan.Point).ConfigureAwait(false);
                // MoveToward(Pawn) natively follows the same target. Interrupt fixed waypoints only after significant deviation from the new target.
                PatrolPawn? own = ReadPawn();
                bool targetMoved = plan.Mode == AfdevBioBotNavigationMode.FindPath &&
                    initial.TargetLocation != null && current.TargetLocation != null && own != null &&
                    DistanceSquared(initial.TargetLocation, current.TargetLocation) >= 1024 * 1024 &&
                    points!.TryGetValue(plan.Goal, out PatrolPoint? point) &&
                    Math.Sqrt(DistanceSquared(point.Location, current.TargetLocation)) >
                        Math.Sqrt(DistanceSquared(own.Location, current.TargetLocation)) + 512;

                bool unsafeEvasion = plan.Evading && plan.Point is { } escape && own != null &&
                    AfdevBioBotEvasion.HorizontalDistanceSquared(ToPoint(own.Location), escape) > 64 * 64 &&
                    !AfdevBioBotEvasion.IsSafeDirection(ToPoint(own.Location), escape, current.ThreatLocations);

                if (current.Revision != initial.Revision || current.HoldPosition || targetMoved || unsafeEvasion)
                {
                    save("behavior-cancel", new RunRouteAsyncJson1 { routes = routes, plan = plan, initial = initial, current = current,
                        Reason = current.Revision != initial.Revision ? "目标或原生生命周期改变。"
                            : current.HoldPosition ? "行为要求停止移动。"
                            : unsafeEvasion ? "敌人移动后，原避让方向已不再安全。" : "固定路线明显偏离移动后的目标。" });
                    routeStopping.Cancel();
                    break;
                }
            }

            return await running.ConfigureAwait(false);
        }
        finally
        {
            // Even after combat-sampling failure, collect every route result started by this task so no command-publishing background task survives.
            routeStopping.Cancel();

            AfdevBioBotRouteResult retired = await running.ConfigureAwait(false);

            if (retired.RequiresProcessStop)
                throw new InvalidOperationException("BIO 行为路线未确认退休：" + retired.Reason);
        }
    }

    private PatrolPawn? ReadPawn()
    {
        uint pawnField = Field(participant.Controller, "Engine.Controller.Pawn");
        uint pawn = reader.U32(pawnField);

        if (pawn < 0x10000 || process.ReadMemory(Field(participant.Controller,
                "Engine.PlayerController.bIgnoreMoveInput", "ByteProperty", 1), 1)[0] != 0)
            return null;

        try
        {
            QueryObject identity = reader.Identity(pawn);

            if ((reader.U32(pawn + 0x0C) & 0x20000000) != 0 || (reader.U32(pawn + 0xB0) & 8) != 0 ||
                unchecked((int)reader.U32(Field(pawn, "Engine.Pawn.Health", "IntProperty"))) <= 0 ||
                reader.U32(Field(pawn, "Engine.Pawn.Controller")) != participant.Controller)
                return null;

            uint canMove = Field(world.GameInfo, "UTGame.TGRoundMatch.bCanMove", "BoolProperty");
            uint moveProperty = reader.FindField(world.GameInfoClass, "bCanMove", "UTGame.TGRoundMatch.bCanMove");
            uint inRound = Field(world.GameInfo, "UTGame.TGRoundMatch.bInRound", "BoolProperty");
            uint roundProperty = reader.FindField(world.GameInfoClass, "bInRound", "UTGame.TGRoundMatch.bInRound");

            if ((reader.U32(canMove) & reader.U32(moveProperty + 0x84)) == 0 ||
                (reader.U32(inRound) & reader.U32(roundProperty + 0x84)) == 0)
                return null;
            if (reader.U32(Field(pawn, "Engine.Actor.WorldInfo")) != world.WorldInfo ||
                reader.U32(Field(pawn, "Engine.Pawn.PlayerReplicationInfo")) != participant.Pri ||
                process.ReadMemory(Field(pawn, "Engine.Actor.Role", "ByteProperty", 1), 1)[0] != 3)
                throw new InvalidDataException("巡逻 Pawn 不属于本局原生权威参赛者。");

            uint cylinder = reader.U32(Field(pawn, "Engine.Pawn.CylinderComponent", "ComponentProperty"));
            float radius = Float(Field(cylinder, "Engine.CylinderComponent.CollisionRadius", "FloatProperty"));
            float height = Float(Field(cylinder, "Engine.CylinderComponent.CollisionHeight", "FloatProperty"));
            float[] location = Location(pawn);
            uint anchor = reader.U32(Field(pawn, "Engine.Pawn.Anchor"));

            if (radius <= 0 || height <= 0)
                return null;
            if (reader.U32(pawnField) != pawn || reader.Identity(pawn) != identity)
                return null;

            return new PatrolPawn(identity, location, anchor, radius, height);
        }
        catch (Exception) when (process.IsAlive && reader.U32(pawnField) != pawn)
        {
            return null;
        }
    }

    private Dictionary<uint, PatrolPoint> ReadPoints()
    {
        uint level = reader.U32(world.World + 0x50);
        uint data = reader.U32(level + 0x3C);
        uint count = reader.U32(level + 0x40);
        var result = new Dictionary<uint, PatrolPoint>();
        var classes = new Dictionary<uint, bool>();

        if (data < 0x10000 || count is 0 or > 200000 || reader.U32(level + 0x44) < count)
            throw new InvalidDataException("巡逻导航点的世界 Actor 数组边界无效。");

        byte[] actors = process.ReadMemory(data, checked((int)count * 4));

        for (int index = 0; index < count; index++)
        {
            uint actor = BinaryPrimitives.ReadUInt32LittleEndian(actors.AsSpan(index * 4, 4));

            if (actor == 0)
                continue;

            uint type = reader.U32(actor + 0x34);

            if (!classes.TryGetValue(type, out bool navigationPoint))
                classes.Add(type, navigationPoint = reader.IsA(actor, "Engine.NavigationPoint"));
            if (!navigationPoint || (reader.U32(actor + 0x0C) & 0x20000000) != 0 || (reader.U32(actor + 0xB0) & 8) != 0)
                continue;
            if (reader.U32(Field(actor, "Engine.Actor.WorldInfo")) != world.WorldInfo)
                continue;

            result.Add(actor, new PatrolPoint(reader.Identity(actor), Location(actor)));
        }

        checkBinding();
        save("patrol-map", new ReadPointsJson1 { World = world.World, NavigationPoints = result.Count });
        return result;
    }

    private PatrolPoint? ChooseGoal(PatrolPawn pawn)
    {
        PatrolPoint[] nearby = points!.Values.OrderBy(point => DistanceSquared(point.Location, pawn.Location)).ToArray();
        PatrolPoint? anchor = FindAnchor(pawn);
        uint anchorAddress = anchor?.Identity.Address ?? 0;
        var depths = new Dictionary<uint, int>();

        if (anchor != null)
        {
            var pending = new Queue<uint>();

            depths.Add(anchor.Identity.Address, 0);
            pending.Enqueue(anchor.Identity.Address);

            // Map size is bounded. Traverse the full connected region; the executor still controls individual-action and route budgets.
            while (pending.TryDequeue(out uint start) && depths.Count < 128)
            {
                foreach (uint end in ReadNeighbors(start, pawn))
                {
                    if (!points.ContainsKey(end) || !depths.TryAdd(end, depths[start] + 1))
                        continue;

                    pending.Enqueue(end);
                }
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return nearby.Where(point => DistanceSquared(point.Location, pawn.Location) >= 96 * 96 &&
                (!retryAfter.TryGetValue(point.Identity.Address, out DateTimeOffset after) || after <= now) &&
                (!noPathOrigins.TryGetValue((anchorAddress, point.Identity.Address), out float[]? origin) ||
                    DistanceSquared(pawn.Location, origin) >= 256 * 256))
            // Prefer targets with explicit usable links from the current anchor so unvisited nodes behind walls cannot crowd out walkable routes.
            .OrderBy(point => depths.GetValueOrDefault(point.Identity.Address) > 0 ? 0 : 1)
            .ThenBy(point => visits.GetValueOrDefault(point.Identity.Address))
            .ThenBy(point => depths.GetValueOrDefault(point.Identity.Address) >= 3 ? 0 : 1)
            .ThenBy(point => DistanceSquared(point.Location, pawn.Location))
            .FirstOrDefault();
    }

    private PatrolPoint? FindAnchor(PatrolPawn pawn)
    {
        Dictionary<uint, PatrolPoint> navigationPoints = points ??
            throw new InvalidOperationException("选取巡逻锚点前必须读取当前世界导航点。");

        PatrolPoint? nativeAnchor = navigationPoints.GetValueOrDefault(pawn.Anchor);

        return nativeAnchor != null && Math.Abs(nativeAnchor.Location[2] - pawn.Location[2]) <= 128 &&
            DistanceSquared(nativeAnchor.Location, pawn.Location) <= 768 * 768 ? nativeAnchor :
            navigationPoints.Values.Where(point => DistanceSquared(point.Location, pawn.Location) <= 1800 * 1800)
                .MinBy(point => DistanceSquared(point.Location, pawn.Location));
    }

    private void RememberNoPath(PatrolPawn attemptedPawn, PatrolPoint goal)
    {
        PatrolPawn? current = ReadPawn();

        if (current == null || current.Identity != attemptedPawn.Identity)
            return;

        uint anchor = FindAnchor(current)?.Identity.Address ?? 0;

        // Isolate targets by actual failure position, not route origin. Retry native judgment only after changing anchor or moving significantly.
        noPathOrigins[(anchor, goal.Identity.Address)] = current.Location;
        save("patrol-no-path", new RememberNoPathJson1 { Anchor = anchor, Goal = goal.Identity, Identity = current.Identity, Location = current.Location });
    }

    private IEnumerable<uint> ReadNeighbors(uint point, PatrolPawn pawn)
    {
        uint list = Field(point, "Engine.NavigationPoint.PathList", "ArrayProperty", 12);
        uint data = reader.U32(list);
        uint count = reader.U32(list + 4);
        uint capacity = reader.U32(list + 8);

        if (count > capacity || capacity > 256 || count != 0 && data < 0x10000)
            throw new InvalidDataException("巡逻导航连接数组无效。");

        for (uint index = 0; index < count; index++)
        {
            uint spec = reader.U32(data + index * 4);

            reader.Identity(spec);

            if (reader.U32(Field(spec, "Engine.ReachSpec.Start")) != point ||
                unchecked((int)reader.U32(Field(spec, "Engine.ReachSpec.CollisionRadius", "IntProperty"))) < pawn.Radius ||
                unchecked((int)reader.U32(Field(spec, "Engine.ReachSpec.CollisionHeight", "IntProperty"))) < pawn.Height ||
                reader.U32(Field(spec, "Engine.ReachSpec.reachFlags", "IntProperty")) != 1 ||
                process.ReadMemory(Field(spec, "Engine.ReachSpec.bPruned", "ByteProperty", 1), 1)[0] != 0)
                continue;

            uint disabled = Field(spec, "Engine.ReachSpec.bDisabled", "BoolProperty");
            uint property = reader.FindField(reader.Identity(spec).Class, "bDisabled", "Engine.ReachSpec.bDisabled");

            if ((reader.U32(disabled) & reader.U32(property + 0x84)) == 0)
            {
                uint end = Field(spec, "Engine.ReachSpec.End", "StructProperty", 20);
                uint endProperty = reader.FindField(reader.Identity(spec).Class, "End", "Engine.ReachSpec.End");
                uint referenceType = reader.U32(endProperty + 0x84);
                uint actorProperty = reader.FindField(referenceType, "Actor", "Engine.Actor.ActorReference.Actor");

                if (reader.Path(referenceType) != "Engine.Actor.ActorReference" || reader.U32(referenceType + 0x50) != 20 ||
                    reader.U32(actorProperty + 0x60) != 0 || reader.U32(actorProperty + 0x44) != 4)
                    throw new InvalidDataException("导航目标 ActorReference 布局不符。");

                yield return reader.U32(end);
            }
        }
    }

    private uint Field(uint receiver, string path, string kind = "ObjectProperty", uint size = 4)
    {
        uint type = reader.U32(receiver + 0x34);

        if (!fields.TryGetValue((type, path), out uint offset))
        {
            offset = reader.FieldAddress(receiver, path[(path.LastIndexOf('.') + 1)..], path, kind, size) - receiver;
            fields.Add((type, path), offset);
        }

        return receiver + offset;
    }

    private float Float(uint address)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(address, 4));

        return float.IsFinite(value) ? value : throw new InvalidDataException("巡逻原生浮点值不是有限数。");
    }

    private float[] Location(uint actor)
    {
        uint address = Field(actor, "Engine.Actor.Location", "StructProperty", 12);

        return [Float(address), Float(address + 4), Float(address + 8)];
    }

    private static double DistanceSquared(float[] a, float[] b) =>
        Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2);

    private sealed record PatrolPoint(QueryObject Identity, float[] Location);
    private sealed record PatrolPawn(QueryObject Identity, float[] Location, uint Anchor, float Radius, float Height);
}
