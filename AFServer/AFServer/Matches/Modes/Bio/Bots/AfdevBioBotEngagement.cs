using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Low-frequency engagement decisions for one bot. Enemies come only from frozen human slots and prepared
/// bots in this match. Read teams, weapons, and round state from native objects each time. Proposes
/// pursuit, evasion, and firing only; BIO controllers/current weapons own hits, reloads, infection, and
/// outcomes.
/// </summary>
internal sealed partial class AfdevBioBotEngagement(AfdevBioBotCombat combat, AfdevOwnedProcess process,
    AfdevNativeEvidence world, AfdevBioBotPreparation participant,
    Func<IReadOnlyList<AfdevBioBotPreparation>> getParticipants, IReadOnlyList<AfdevBioBotHumanIdentity> humans,
    Action<string, object> save, Action checkBinding)
{
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly Dictionary<(uint Class, string Path), uint> fields = [];
    private readonly Dictionary<(uint Class, string Path), uint> masks = [];
    private readonly HashSet<string> humanUins = humans.Where(human => !human.Observer)
        .Select(human => human.Uin.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
    private CombatLifecycle? lifecycle;
    private QueryObject? targetIdentity;
    private bool firing;
    private long revision;
    private DateTimeOffset burstUntil;
    private DateTimeOffset nextFireAt;
    private DateTimeOffset nextReloadAt;
    private DateTimeOffset nextSwitchAt;
    private readonly Dictionary<QueryObject, DateTimeOffset> deferredTargets = [];
    private readonly Dictionary<string, long> decisionCounts = [];
    private long decisionSequence;
    private string? lastDecisionReason;
    private DateTimeOffset nextDecisionReport;

    public async Task<AfdevBioBotCombatDecision> UpdateAsync(CancellationToken token, bool navigationActive = false,
        AfdevBioBotPoint? destination = null)
    {
        token.ThrowIfCancellationRequested();
        checkBinding();

        AfdevBioBotCombatSnapshot? current = combat.ReadSnapshot();
        int round = unchecked((int)reader.U32(Field(world.GameInfo, "UTGame.TGRoundMatch.RoundCount", "IntProperty")));
        bool inRound = Bit(world.GameInfo, "UTGame.TGRoundMatch.bInRound");
        bool roundStarted = Bit(world.GameInfo, "TGBioGame.TGBioMatch.bRoundStarted");
        var observed = new CombatLifecycle(current?.Pawn, current?.Weapon,
            current == null ? -1 : unchecked((int)current.TeamIndex), round, inRound, roundStarted, current?.CanAct == true);
        bool contextChanged = lifecycle != observed;

        if (contextChanged)
        {
            // Retire old firing intent before navigation observes a new generation. On Pawn replacement, the stop gate never dereferences the old object.
            await StopAsync().ConfigureAwait(false);
            lifecycle = observed;
            revision++;
            nextFireAt = DateTimeOffset.MinValue;
            nextReloadAt = DateTimeOffset.MinValue;
            nextSwitchAt = DateTimeOffset.MinValue;
            nextSkillAt = DateTimeOffset.MinValue;
            nextHeroAt = DateTimeOffset.MinValue;
            evading = false;
            deferredTargets.Clear();
            save("behavior-lifecycle", new UpdateAsyncJson1 { Controller = participant.Controller, Pri = participant.Pri, revision = revision, observed = observed });
        }

        nearbyThreats = [];

        CombatTarget? target = current is { CanAct: true } && inRound && roundStarted
            ? ReadTarget(current) : null;

        if (targetIdentity != target?.Identity)
        {
            await CeaseFireAsync().ConfigureAwait(false);
            targetIdentity = target?.Identity;
            revision++;
            contextChanged = true;
            save("behavior-target", new UpdateAsyncJson2 { revision = revision, Pawn = current?.Pawn, Target = targetIdentity, Round = round });
        }

        if (current is { CanAct: true } && inRound && roundStarted && !contextChanged)
        {
            AfdevBioBotCombatDecision? ability = await UpdateAbilitiesAsync(current, target, navigationActive, token).ConfigureAwait(false);

            if (ability != null)
                return ability;

            AfdevBioBotCombatDecision? evasion = await UpdateEvasionAsync(current, target, navigationActive,
                destination, token).ConfigureAwait(false);

            if (evasion != null)
                return evasion;
        }

        if (current == null || target == null)
        {
            if (navigationActive && current is { CanAct: true })
                await FaceMovementAsync(current, token, destination).ConfigureAwait(false);

            return Decision(current, null, false, current == null ? "PawnOrWeaponUnavailable" : "NoEnemyOrRoundWaiting");
        }
        if (contextChanged)
            return Decision(current, target, false, "ContextChanged");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Handle empty ammunition first so lack of visibility or range cannot leave the bot holding an unusable gun indefinitely.
        if (!current.IsMelee && current.Ammo <= 0)
        {
            await CeaseFireAsync().ConfigureAwait(false);

            if (current.CarriedAmmo > 0 && now >= nextReloadAt)
            {
                await combat.ExecuteAsync(AfdevBioBotCombatAction.Reload, current,
                    cancellationToken: token).ConfigureAwait(false);
                nextReloadAt = now.AddSeconds(2);
            }

            if (current.CarriedAmmo <= 0 && now >= nextSwitchAt)
            {
                // Select an armed weapon or melee from the current native inventory, preserving equipment-change and animation lifetimes.
                await combat.ExecuteAsync(AfdevBioBotCombatAction.SwitchToBestWeapon, current,
                    cancellationToken: token).ConfigureAwait(false);
                nextSwitchAt = now.AddSeconds(2);
            }

            return Decision(current, target, current.CarriedAmmo > 0,
                current.CarriedAmmo > 0 ? "WaitingForReload" : "SelectingUsableWeapon");
        }

        double engagementRange = AttackAttemptRange(current, target);

        if (DistanceSquared(current.Location, target.Location) > engagementRange * engagementRange)
        {
            await CeaseFireAsync().ConfigureAwait(false);

            if (navigationActive)
                await FaceMovementAsync(current, token, destination).ConfigureAwait(false);

            return Decision(current, target, false, "OutsideAttackRange");
        }

        AfdevBioBotCombatResult sight = await combat.ExecuteAsync(AfdevBioBotCombatAction.LineOfSight,
            current, target.Identity.Address, token).ConfigureAwait(false);

        if (!sight.NativeExecuted || sight.Interrupted || sight.BoolResult != true)
        {
            await CeaseFireAsync().ConfigureAwait(false);

            if (navigationActive)
                await FaceMovementAsync(current, token, destination).ConfigureAwait(false);

            return Decision(current, target, false, sight.Interrupted ? "SightInterrupted" : "NoLineOfSight");
        }

        if (navigationActive && !current.IsMelee)
        {
            await CeaseFireAsync().ConfigureAwait(false);
            return Decision(current, target, true, "RetireNavigationForAttack");
        }

        AfdevBioBotCombatResult aim = await combat.ExecuteAsync(AfdevBioBotCombatAction.Aim,
            current, target.Identity.Address, token).ConfigureAwait(false);

        if (!aim.NativeExecuted || aim.Interrupted)
        {
            await CeaseFireAsync().ConfigureAwait(false);
            return Decision(current, target, false, "AimInterrupted");
        }

        AfdevBioBotCombatResult facing = await combat.ExecuteAsync(AfdevBioBotCombatAction.FaceTarget,
            current, target.Identity.Address, token).ConfigureAwait(false);

        if (!facing.NativeExecuted || facing.Interrupted)
        {
            await CeaseFireAsync().ConfigureAwait(false);
            return Decision(current, target, false, "FacingInterrupted");
        }

        if (firing && now >= burstUntil)
        {
            await CeaseFireAsync().ConfigureAwait(false);
            nextFireAt = now.AddMilliseconds(current.IsMelee ? 250 : 300);
        }

        if (!firing && now >= nextFireAt)
        {
            // Aim returned in an earlier game Tick; StartFire uses the natively cached viewpoint on a later Tick.
            AfdevBioBotCombatResult started = await combat.ExecuteAsync(AfdevBioBotCombatAction.StartFire,
                current, target.Identity.Address, token).ConfigureAwait(false);

            firing = started.NativeExecuted && !started.Interrupted;
            burstUntil = DateTimeOffset.UtcNow.AddMilliseconds(current.IsMelee ? 350 : 650);
        }

        // Melee permits approaching while swinging. Native code determines hits; entering attempt range alone must not stop pursuit.
        return Decision(current, target, !current.IsMelee, firing ? "FireRequested" : "AttackCooldown");
    }

    /// <summary>
    /// The sole behavior task turns before a coordinate route begins so short segments do not miss periodic
    /// samples. Preserve an aim held for claw attacks. On full Pawn-generation change or command rejection,
    /// return to higher-level replanning without actions for the old character.
    /// </summary>
    public async Task<bool> PrepareDestinationAsync(QueryObject expectedPawn, AfdevBioBotPoint destination,
        CancellationToken token)
    {
        checkBinding();

        AfdevBioBotCombatSnapshot? current = combat.ReadSnapshot();

        if (current is not { CanAct: true } || current.Pawn != expectedPawn)
            return false;
        if (firing)
            return true;

        AfdevBioBotCombatResult result = await FaceMovementAsync(current, token, destination).ConfigureAwait(false);

        return result.NativeExecuted && !result.Interrupted;
    }

    public async Task StopAsync()
    {
        await combat.StopAsync(CancellationToken.None).ConfigureAwait(false);
        firing = false;
    }

    /// <summary>
    /// Temporarily changes targets after repeated navigation stalls, isolated by exact Pawn identity and
    /// cleared on lifetime changes.
    /// </summary>
    public void DeferTarget(uint pawn, string reason)
    {
        if (targetIdentity?.Address != pawn)
            return;

        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(10);

        deferredTargets[targetIdentity] = until;
        save("behavior-target-deferred", new DeferTargetJson1 { Target = targetIdentity, until = until, reason = reason });
    }

    private async Task CeaseFireAsync()
    {
        if (firing)
            await StopAsync().ConfigureAwait(false);
    }

    private Task<AfdevBioBotCombatResult> FaceMovementAsync(AfdevBioBotCombatSnapshot current, CancellationToken token,
        AfdevBioBotPoint? destination) => combat.ExecuteAsync(destination.HasValue
            ? AfdevBioBotCombatAction.FaceDestination : AfdevBioBotCombatAction.FaceMovement,
            current, cancellationToken: token, destination: destination);

    private AfdevBioBotCombatDecision Decision(AfdevBioBotCombatSnapshot? current, CombatTarget? target, bool hold, string reason)
    {
        var decision = new AfdevBioBotCombatDecision(revision, current?.Pawn.Address ?? 0,
            target?.Identity.Address ?? 0, target?.Location, target?.Anchor ?? 0, hold)
        {
            Evading = evading,
            ThreatLocations = evading ? nearbyThreats.Select(item =>
                new AfdevBioBotPoint(item.Location[0], item.Location[1], item.Location[2])).ToArray() : []
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;

        decisionCounts[reason] = decisionCounts.GetValueOrDefault(reason) + 1;

        if (reason != lastDecisionReason || now >= nextDecisionReport)
        {
            // Bounded coverage windows retain specific branches; cumulative counters are separate. Command return is not hit evidence.
            save("behavior-decision-" + ++decisionSequence % 32, new DecisionJson1
            {
                Sequence = decisionSequence, ObservedAtUtc = now, Reason = reason, decision = decision,
                Snapshot = current, Target = target,
                Distance = current != null && target != null ? Math.Sqrt(DistanceSquared(current.Location, target.Location)) : (double?)null,
                AttackAttemptRange = current != null && target != null ? AttackAttemptRange(current, target) : (double?)null,
                FireIntentActive = firing, AttackHitVerified = false
            });
            save("behavior-decision-counts", new DecisionJson2 { ObservedAtUtc = now, Counts = decisionCounts,
                Limitation = "决策采样次数，不是攻击次数或命中次数；具体快照为覆盖窗口。" });
            lastDecisionReason = reason;
            nextDecisionReport = now.AddSeconds(2);
        }

        return decision;
    }

    private CombatTarget? ReadTarget(AfdevBioBotCombatSnapshot current)
    {
        int ownTeam = unchecked((int)current.TeamIndex);
        float[] ownLocation = current.Location;
        double immediateRange = current.IsMelee ? Math.Max(current.WeaponRange + 64, 160) : 384;

        if (ownTeam is not (0 or 1))
            return null;

        var targets = new List<CombatTarget>();

        foreach (AfdevBioBotPreparation candidate in getParticipants())
        {
            if (candidate.BotId == Guid.Empty || candidate.InstanceId != participant.InstanceId ||
                candidate.AttemptId != participant.AttemptId ||
                candidate.AllocationId != participant.AllocationId || candidate.WorldGeneration != participant.WorldGeneration ||
                candidate.ProcessId != participant.ProcessId || candidate.ProcessCreationTimeUtc != participant.ProcessCreationTimeUtc ||
                candidate.World != world.World || candidate.GameInfo != world.GameInfo)
                throw new InvalidDataException("交战候选 Bot 不属于同一进程、对局与世界代际。");
            if (candidate == participant)
                continue;
            if (candidate.BotId == participant.BotId || candidate.Controller == participant.Controller || candidate.Pri == participant.Pri)
                throw new InvalidDataException("不同交战候选 Bot 复用了本控制器的内部身份或原生对象。");

            CombatTarget? target = ReadParticipantTarget(candidate.Controller, candidate.Pri, candidate.Uin, ownTeam, 0);

            if (target != null)
                targets.Add(target);
        }

        targets.AddRange(ReadHumanTargets(ownTeam));

        // Evasion retains positions of all nearby enemies. Pursuit-failure cooldowns must not hide nearby threats.
        nearbyThreats = targets.Where(target => AfdevBioBotEvasion.IsNearby(
            new AfdevBioBotPoint(ownLocation[0], ownLocation[1], ownLocation[2]),
            new AfdevBioBotPoint(target.Location[0], target.Location[1], target.Location[2]),
            AfdevBioBotEvasion.ReleaseDistance)).ToArray();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (QueryObject expired in deferredTargets.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            deferredTargets.Remove(expired);

        CombatTarget[] available = targets.Where(target => !deferredTargets.ContainsKey(target.Identity) ||
            DistanceSquared(ownLocation, target.Location) <= immediateRange * immediateRange).ToArray();
        CombatTarget? retained = available.FirstOrDefault(target => target.Identity == targetIdentity);
        CombatTarget? nearest = available.MinBy(target => DistanceSquared(ownLocation, target.Location));

        if (retained == null || nearest == null)
            return nearest;

        double retainedDistance = Math.Sqrt(DistanceSquared(ownLocation, retained.Location));
        double nearestDistance = Math.Sqrt(DistanceSquared(ownLocation, nearest.Location));

        // Prefer melee targets immediately within attempt range over nearby enemies that cannot be reached by a claw attack.
        // Otherwise preserve pursuit stability. Failure cooldowns cannot hide an enemy approaching within reach.
        return current.IsMelee && nearestDistance <= AttackAttemptRange(current, nearest) &&
                retainedDistance > AttackAttemptRange(current, retained) ||
            nearestDistance <= immediateRange && retainedDistance > immediateRange * 1.25 ||
            nearestDistance + 96 < retainedDistance * 0.65 ? nearest : retained;
    }

    private IReadOnlyList<CombatTarget> ReadHumanTargets(int ownTeam)
    {
        if (humanUins.Count == 0)
            return [];
        if (reader.U32(world.World + 0xD8) != world.NetDriver)
            throw new InvalidDataException("交战采样的 NetDriver 已不属于本局世界。");

        // Uses the ClientConnections layout validated by the activation gate. Connection is a network object, not a UObject.
        byte[] header = process.ReadMemory(world.NetDriver + 0x44, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));

        if (count > capacity || capacity > 64 || count != 0 && !IsPointer(data))
            throw new InvalidDataException("交战采样的原生连接数组超出已核实边界。");
        if (count == 0)
            return [];

        byte[] connections = process.ReadMemory(data, checked((int)count * 4));
        var targets = new List<CombatTarget>();

        bool Unchanged() => reader.U32(world.World + 0xD8) == world.NetDriver &&
            process.ReadMemory(world.NetDriver + 0x44, 12).AsSpan().SequenceEqual(header) &&
            process.ReadMemory(data, connections.Length).AsSpan().SequenceEqual(connections);

        try
        {
            for (int index = 0; index < connections.Length; index += 4)
            {
                uint connection = BinaryPrimitives.ReadUInt32LittleEndian(connections.AsSpan(index));

                if (!IsPointer(connection))
                    continue;

                CombatTarget? target = ReadHumanTarget(connection, ownTeam);

                if (target != null)
                    targets.Add(target);
            }

            // Connection-array changes discard only this human sample; one departing human must not stop other bots' behavior tasks.
            return Unchanged() ? targets : [];
        }
        catch (Exception) when (process.IsAlive && !Unchanged())
        {
            return [];
        }
    }

    private CombatTarget? ReadHumanTarget(uint connection, int ownTeam)
    {
        uint controller = reader.U32(connection + 0x40);

        if (!IsPointer(controller) || reader.U32(connection + 0x60) != world.NetDriver)
            return null;

        try
        {
            if (!reader.IsA(controller, "TGBioGame.TGBioPlayerController"))
                return null;

            uint pri = reader.U32(Field(controller, "Engine.Controller.PlayerReplicationInfo"));

            if (!IsPointer(pri) || !reader.IsA(pri, "TGBioGame.TGBioPlayerReplicationInfo"))
                return null;

            string? uin = ReadUin(pri);
            CombatTarget? target = uin != null && humanUins.Contains(uin)
                ? ReadParticipantTarget(controller, pri, uin, ownTeam, connection) : null;

            return reader.U32(connection + 0x40) == controller &&
                reader.U32(connection + 0x60) == world.NetDriver ? target : null;
        }
        catch (Exception) when (process.IsAlive && reader.U32(connection + 0x40) != controller)
        {
            return null;
        }
    }

    private CombatTarget? ReadParticipantTarget(uint controller, uint pri, string expectedUin, int ownTeam, uint connection)
    {
        QueryObject controllerIdentity = reader.Identity(controller);
        QueryObject priIdentity = reader.Identity(pri);
        uint priField = Field(controller, "Engine.Controller.PlayerReplicationInfo");
        uint pawnField = Field(controller, "Engine.Controller.Pawn");
        uint pawn = reader.U32(pawnField);

        if (!IsPointer(pawn) || controller == participant.Controller || pri == participant.Pri)
            return null;

        try
        {
            if (Deleted(controller) || Deleted(pri) || reader.U32(priField) != pri ||
                reader.U32(Field(controller, "Engine.Actor.WorldInfo")) != world.WorldInfo ||
                reader.U32(Field(pri, "Engine.Actor.WorldInfo")) != world.WorldInfo ||
                reader.U32(Field(pri, "Engine.Actor.Owner")) != controller ||
                reader.U32(Field(controller, "Engine.PlayerController.Player")) != connection ||
                Bit(pri, "Engine.PlayerReplicationInfo.bBot") != (connection == 0) ||
                ReadUin(pri) != expectedUin)
                return null;
            if (Bit(pri, "Engine.PlayerReplicationInfo.bOnlySpectator") ||
                Bit(pri, "Engine.PlayerReplicationInfo.bIsSpectator") ||
                Bit(pri, "UTGame.TGPlayerReplicationInfo.bAlwaysObserver") ||
                Bit(pri, "UTGame.TGPlayerReplicationInfo.bIsInternalDemoRecPlayer") ||
                Bit(pri, "UTGame.TGPlayerReplicationInfo.bGameMaster"))
                return null;

            QueryObject identity = reader.Identity(pawn);

            if (Deleted(pawn) ||
                unchecked((int)reader.U32(Field(pawn, "Engine.Pawn.Health", "IntProperty"))) <= 0 ||
                reader.U32(Field(pawn, "Engine.Pawn.Controller")) != controller)
                return null;
            if (reader.U32(Field(pawn, "Engine.Actor.WorldInfo")) != world.WorldInfo ||
                reader.U32(Field(pawn, "Engine.Pawn.PlayerReplicationInfo")) != pri)
                return null;

            uint team = reader.U32(Field(pri, "Engine.PlayerReplicationInfo.Team"));

            if (team < 0x10000)
                return null;

            int teamIndex = unchecked((int)reader.U32(Field(team, "Engine.TeamInfo.TeamIndex", "IntProperty")));

            if (teamIndex is not (0 or 1) || teamIndex == ownTeam)
                return null;

            uint locationField = Field(pawn, "Engine.Actor.Location", "StructProperty", 12);
            byte[] locationBytes = process.ReadMemory(locationField, 12);
            float[] location = [BinaryPrimitives.ReadSingleLittleEndian(locationBytes),
                BinaryPrimitives.ReadSingleLittleEndian(locationBytes.AsSpan(4)),
                BinaryPrimitives.ReadSingleLittleEndian(locationBytes.AsSpan(8))];
            uint anchor = reader.U32(Field(pawn, "Engine.Pawn.Anchor"));
            uint cylinder = reader.U32(Field(pawn, "Engine.Pawn.CylinderComponent", "ComponentProperty"));

            if (!IsPointer(cylinder))
                return null;

            float radius = BitConverter.Int32BitsToSingle(unchecked((int)reader.U32(
                Field(cylinder, "Engine.CylinderComponent.CollisionRadius", "FloatProperty"))));

            if (location.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("交战目标位置包含非有限数。");
            if (!float.IsFinite(radius) || radius is <= 0 or > 256)
                return null;
            if (reader.U32(pawnField) != pawn || reader.U32(priField) != pri ||
                reader.Identity(pawn) != identity || reader.Identity(controller) != controllerIdentity ||
                reader.Identity(pri) != priIdentity)
                return null;

            return new CombatTarget(identity, location, anchor, radius);
        }
        catch (Exception) when (process.IsAlive &&
            (reader.U32(pawnField) != pawn || reader.U32(priField) != pri || Deleted(controller) || Deleted(pri)))
        {
            return null;
        }
    }

    private string? ReadUin(uint pri)
    {
        uint address = Field(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12);
        byte[] header = process.ReadMemory(address, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));

        if (count > capacity || count > 33 || capacity >= 65536 || count != 0 && !IsPointer(data))
            throw new InvalidDataException("交战目标的完整 UIN 字符串超出已核实边界。");
        if (count == 0)
            return null;

        byte[] text = process.ReadMemory(data, checked((int)count * 2));

        if (!process.ReadMemory(address, 12).AsSpan().SequenceEqual(header))
            return null;
        if (text[^1] != 0 || text[^2] != 0)
            throw new InvalidDataException("交战目标的完整 UIN 字符串缺少终止符。");

        return Encoding.Unicode.GetString(text, 0, text.Length - 2);
    }

    private bool Deleted(uint actor) => (reader.U32(actor + 0x0C) & 0x20000000) != 0 ||
        (reader.U32(actor + 0xB0) & 8) != 0;

    private static bool IsPointer(uint address) => address is >= 0x10000 and < 0xFFF00000 && (address & 3) == 0;

    private bool Bit(uint receiver, string path)
    {
        uint address = Field(receiver, path, "BoolProperty");
        uint type = reader.U32(receiver + 0x34);

        if (!masks.TryGetValue((type, path), out uint mask))
        {
            uint property = reader.FindField(type, path[(path.LastIndexOf('.') + 1)..], path);

            mask = reader.U32(property + 0x84);

            if (mask == 0 || (mask & (mask - 1)) != 0)
                throw new InvalidDataException("战斗回合门不是反射的单一布尔位：" + path);

            masks.Add((type, path), mask);
        }

        return (reader.U32(address) & mask) != 0;
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

    private static double DistanceSquared(float[] left, float[] right) =>
        Math.Pow(left[0] - right[0], 2) + Math.Pow(left[1] - right[1], 2) + Math.Pow(left[2] - right[2], 2);

    private static double AttackAttemptRange(AfdevBioBotCombatSnapshot current, CombatTarget target)
    {
        double range = current.WeaponRange > 0 && double.IsFinite(current.WeaponRange)
            ? current.WeaponRange : current.IsMelee ? 140 : 1800;

        // TG melee server-side swept arcs use 1.4 times native reach and intersect target collision volumes.
        // This controls only when to attempt attacks. The current weapon still handles actual trajectories, obstruction, hits, and damage.
        return current.IsMelee ? Math.Min(range * 1.4 + target.Radius, 256) : range;
    }

    private sealed record CombatLifecycle(QueryObject? Pawn, QueryObject? Weapon, int TeamIndex,
        int RoundCount, bool InRound, bool RoundStarted, bool CanAct);
    private sealed record CombatTarget(QueryObject Identity, float[] Location, uint Anchor, float Radius);
}

/// <summary>
/// Object generation and movement intent from one behavior sample. Evasion positions guide planning only;
/// native reachability checks still govern movement.
/// </summary>
internal sealed record AfdevBioBotCombatDecision(long Revision, uint Pawn, uint TargetPawn,
    float[]? TargetLocation, uint TargetAnchor, bool HoldPosition)
{
    public bool Evading { get; init; }
    public IReadOnlyList<AfdevBioBotPoint> ThreatLocations { get; init; } = [];
}
