using System.Buffers.Binary;
using System.Diagnostics;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Reusable combat-command slot for one bot. All actions invoke validated scripts on the original thread;
/// the host reads snapshots and publishes intent only. Normal death, infection, or weapon replacement
/// retires old intent. Unknown native completion or ownership failure requires reclaiming the original Job.
/// </summary>
internal sealed partial class AfdevBioBotCombat(AfdevOwnedProcess process, AfdevNativeEvidence world,
    AfdevBioBotPreparation participant, Guid botId, uint onlinePage, uint command,
    Action<string, object> save, Action checkBinding)
{
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly AfdevBioBotCombatFunctions functions = new(process, world, participant.Controller);
    private readonly SemaphoreSlim gate = new(1, 1);
    private QueryObject? previousPawn;
    private QueryObject? previousWeapon;
    private AfdevBioBotCombatSnapshot? firing;
    private long pawnGeneration;
    private long weaponGeneration;
    private uint ordinal;
    private bool poisoned;

    /// <summary>
    /// Reads the current controller, Pawn, and equipment. Null means native spawn/generation is unstable;
    /// never creates replacements.
    /// </summary>
    public AfdevBioBotCombatSnapshot? ReadSnapshot()
    {
        checkBinding();

        uint pawnField = Field(participant.Controller, "Engine.Controller.Pawn");
        uint pawn = reader.U32(pawnField);

        if (pawn < 0x10000)
            return null;

        uint weaponField = 0;
        uint weapon = 0;

        try
        {
            QueryObject pawnIdentity = reader.Identity(pawn);
            int health = Int(Field(pawn, "Engine.Pawn.Health", "IntProperty"));

            if (Deleted(pawn) || health <= 0)
                return null;
            if (reader.U32(Field(participant.Controller, "Engine.Controller.PlayerReplicationInfo")) != participant.Pri ||
                reader.U32(Field(pawn, "Engine.Actor.WorldInfo")) != world.WorldInfo)
                throw new InvalidDataException("战斗快照失去本局控制器、PRI 或世界所有权。");
            if (reader.U32(Field(pawn, "Engine.Pawn.Controller")) != participant.Controller ||
                reader.U32(Field(pawn, "Engine.Pawn.PlayerReplicationInfo")) != participant.Pri)
                return null;

            weaponField = Field(pawn, "Engine.Pawn.Weapon");
            weapon = reader.U32(weaponField);

            if (weapon < 0x10000)
                return null;

            QueryObject weaponIdentity = reader.Identity(weapon);

            if (Deleted(weapon))
                return null;
            if (reader.U32(Field(weapon, "Engine.Actor.WorldInfo")) != world.WorldInfo)
                throw new InvalidDataException("战斗装备不属于本局世界。");
            if (reader.U32(Field(weapon, "Engine.Actor.Instigator")) != pawn)
                return null;

            uint inventory = reader.U32(Field(pawn, "Engine.Pawn.InvManager"));
            uint team = reader.U32(Field(participant.Pri, "Engine.PlayerReplicationInfo.Team"));

            if (inventory < 0x10000 || team < 0x10000 ||
                reader.U32(Field(weapon, "Engine.Inventory.InvManager")) != inventory)
                return null;

            uint teamIndex = reader.U32(Field(team, "Engine.TeamInfo.TeamIndex", "IntProperty"));
            uint frame = reader.U32(participant.Controller + 0x18);
            uint state = frame == 0 ? 0 : reader.U32(frame + 0x28);
            bool walking = state != 0 && reader.Path(state).EndsWith(".PlayerWalking", StringComparison.Ordinal);
            bool canMove = Bit(world.GameInfo, "UTGame.TGRoundMatch.bCanMove");
            bool inRound = Bit(world.GameInfo, "UTGame.TGRoundMatch.bInRound");
            bool roundStarted = Bit(world.GameInfo, "TGBioGame.TGBioMatch.bRoundStarted");
            bool melee = reader.IsA(weapon, "UTGame.TGRealMeleeWeapon");
            float range = Float(Field(weapon, melee ? "UTGame.TGRealMeleeWeapon.SwingWeaponRange"
                : "Engine.Weapon.WeaponRange", "FloatProperty"));
            int ammo = Int(Field(weapon, "UDKBase.UDKWeapon.AmmoCount", "IntProperty")) ^
                Int(Field(weapon, "UDKBase.UDKWeapon.AmmoCountKey", "IntProperty"));
            int carried = Int(Field(weapon, "UTGame.TGRealWeapon.CarriedAmmoCount", "IntProperty"));
            float[] location = Location(pawn);
            float eyeHeight = Float(Field(pawn, "Engine.Pawn.BaseEyeHeight", "FloatProperty"));
            uint weaponFrame = reader.U32(weapon + 0x18);
            uint weaponState = weaponFrame == 0 ? 0 : reader.U32(weaponFrame + 0x28);
            string weaponClass = reader.Path(weaponIdentity.Class);
            string? weaponStatePath = weaponState == 0 ? null : reader.Path(weaponState);

            if (reader.U32(pawnField) != pawn || reader.U32(weaponField) != weapon ||
                reader.Identity(pawn) != pawnIdentity || reader.Identity(weapon) != weaponIdentity)
                return null;

            if (previousPawn != pawnIdentity)
            {
                previousPawn = pawnIdentity;
                pawnGeneration++;
            }

            if (previousWeapon != weaponIdentity)
            {
                previousWeapon = weaponIdentity;
                weaponGeneration++;
            }

            return new AfdevBioBotCombatSnapshot(pawnIdentity, weaponIdentity, pawnGeneration, weaponGeneration,
                teamIndex, health, location, eyeHeight, inRound && canMove && walking && health > 0,
                roundStarted, melee, ammo, carried, range, state, inventory, team, weaponClass, weaponStatePath);
        }
        catch (Exception) when (process.IsAlive && (reader.U32(pawnField) != pawn ||
            weaponField != 0 && reader.U32(weaponField) != weapon))
        {
            return null;
        }
    }

    /// <summary>
    /// Executes one intent for the current identity. targetPawn=0 supports movement turns,
    /// original-direction checks, stopping fire, or equipment changes; production enemy selection occurs
    /// above this layer. destination affects this segment's facing only, published through the current
    /// Pawn/weapon and command slot without changing position/velocity. Aim and subsequent StartFire each
    /// complete IdleAck so one Tick cannot consume a stale viewpoint cache.
    /// </summary>
    public async Task<AfdevBioBotCombatResult> ExecuteAsync(AfdevBioBotCombatAction action,
        AfdevBioBotCombatSnapshot expected, uint targetPawn = 0, CancellationToken cancellationToken = default,
        AfdevBioBotPoint? destination = null)
    {
        if ((action == AfdevBioBotCombatAction.FaceDestination) != destination.HasValue ||
            destination is { } point && (!point.IsValid || targetPawn != 0))
            throw new ArgumentException("坐标转向必须只提供当前路段的有效目的地。", nameof(destination));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ExecuteLockedAsync(action, expected, targetPawn, cancellationToken, destination).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops fire repeatedly and retires the current command without closing the slot; subsequent native
    /// Pawns/weapons may reuse it.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!process.IsAlive)
                return;
            if (poisoned)
                throw new InvalidOperationException("战斗槽已有未知完成，必须回收原 Job。");

            await DisarmAsync(cancellationToken).ConfigureAwait(false);

            AfdevBioBotCombatSnapshot? pending = firing;
            AfdevBioBotCombatResult? stopped = null;

            if (pending != null && StillEquipped(pending))
                stopped = await ExecuteLockedAsync(AfdevBioBotCombatAction.StopFire, pending, 0,
                    cancellationToken).ConfigureAwait(false);

            // After Pawn replacement, never call the old weapon merely for cleanup. The command has exited; native death/replacement owns old-equipment lifetime.
            firing = null;
            save("combat-stopped", new StopAsyncJson1 { ordinal = ordinal, stopped = stopped, NativeOwnerRetired = pending != null && stopped == null,
                IdleAck = reader.U32(command + AfdevBioBotCombatHook.IdleAck) });
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AfdevBioBotCombatResult> ExecuteLockedAsync(AfdevBioBotCombatAction action,
        AfdevBioBotCombatSnapshot expected, uint targetPawn, CancellationToken token, AfdevBioBotPoint? destination = null)
    {
        bool requested = false;
        uint next = ordinal + 1;

        try
        {
            if (poisoned || ordinal == uint.MaxValue)
                throw new InvalidOperationException("战斗槽已有未知完成或序号耗尽，必须回收原 Job。");

            checkBinding();
            await DisarmAsync(token).ConfigureAwait(false);

            if (!StillEquipped(expected) || action != AfdevBioBotCombatAction.StopFire &&
                (ReadSnapshot() is not { CanAct: true } current || current.Pawn != expected.Pawn ||
                    current.Weapon != expected.Weapon || current.TeamIndex != expected.TeamIndex))
                return Result(action, expected, targetPawn, 0, false, true, null, 0, 0);

            byte[] payload = Describe(action, expected, targetPawn, next, destination);

            process.WriteMemory(command, payload);

            if (!process.ReadMemory(command, payload.Length).AsSpan().SequenceEqual(payload))
                throw new IOException("战斗描述页发布前回读不一致。");

            token.ThrowIfCancellationRequested();
            requested = true;
            process.WriteMemory(command, [1, 0, 0, 0]);

            var elapsed = Stopwatch.StartNew();

            while (reader.U32(command + 8) == 0)
            {
                token.ThrowIfCancellationRequested();
                checkBinding();

                if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("原生战斗入口未确认返回，禁止重发。");

                await Task.Delay(10, token).ConfigureAwait(false);
            }

            byte[] header = process.ReadMemory(command, 0x40);

            uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset, 4));

            uint status = Word(12);
            bool executed = Word(0x18) == next && Word(0x1C) == next && Word(4) == 1 && Word(0x20) == Word(0x24);
            bool rejected = status == 2 && Word(4) == 0 && Word(0x18) == 0 && Word(0x1C) == 0;

            if (Word(0) != 1 || Word(8) != 1 || status is < 1 or > 3 ||
                !executed && !rejected || reader.U32(command + AfdevBioBotCombatHook.CompletedOrdinal) != next)
                throw new InvalidDataException("战斗入口未取得明确执行或拒绝收据，必须回收原 Job。");

            await DisarmAsync(token).ConfigureAwait(false);
            ordinal = next;

            if (action == AfdevBioBotCombatAction.StartFire && executed)
                firing = expected;
            if (action == AfdevBioBotCombatAction.StopFire && executed)
                firing = null;

            AfdevBioBotCombatResult result = Result(action, expected, targetPawn, next, executed, status != 1,
                status == 1 && action == AfdevBioBotCombatAction.LineOfSight ? Word(0x10) == 1 : null, Word(0x28), Word(0x2C));

            save("combat-result-" + next % 8, new ExecuteLockedAsyncJson1 { result = result, destination = destination, Status = status, HeaderHex = Convert.ToHexString(header) });

            if (action == AfdevBioBotCombatAction.StartFire && executed && status != 1)
            {
                // A hit may synchronously trigger infection or round end. Even after return, stop/clear the action before handing its interruption receipt to scheduling.
                if (StillEquipped(expected))
                    await ExecuteLockedAsync(AfdevBioBotCombatAction.StopFire, expected, 0, CancellationToken.None).ConfigureAwait(false);

                firing = null;
            }

            return result;
        }
        catch (Exception error)
        {
            try
            {
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stopping)
            {
                poisoned = true;
                throw new AggregateException("战斗命令失败且未确认退出，必须回收原 Job。", error, stopping);
            }

            uint started = reader.U32(command + 0x18);
            uint returned = reader.U32(command + 0x1C);
            bool executed = requested && started == next && returned == next && reader.U32(command + 8) == 1 &&
                reader.U32(command + 0x20) == reader.U32(command + 0x24);
            bool settled = !requested || started == 0 && returned == 0 && reader.U32(command + 4) == 0 || executed;

            if (settled && reader.U32(command + AfdevBioBotCombatHook.CompletedOrdinal) == next)
                ordinal = next;
            if (executed && action == AfdevBioBotCombatAction.StartFire)
                firing = expected;
            if (executed && action == AfdevBioBotCombatAction.StopFire)
                firing = null;

            if (settled && (error is AfdevBioBotActionInterruptedException || !StillEquipped(expected) ||
                targetPawn != 0 && !TargetAvailable(targetPawn, expected.TeamIndex)))
                return Result(action, expected, targetPawn, requested ? next : 0, executed, true, null, 0, 0);
            if (settled && error is OperationCanceledException && token.IsCancellationRequested)
                throw;

            poisoned = true;
            save("combat-failed", new ExecuteLockedAsyncJson2 { ordinal = ordinal, action = action, targetPawn = targetPawn, started = started, returned = returned, Message = error.Message });
            throw;
        }
    }

    private AfdevBioBotCombatResult Result(AfdevBioBotCombatAction action, AfdevBioBotCombatSnapshot expected,
        uint target, uint sequence, bool executed, bool interrupted, bool? boolResult, uint startedTick, uint returnedTick) =>
        new(participant.InstanceId, participant.AttemptId, participant.AllocationId, participant.WorldGeneration,
            botId, expected.PawnGeneration, expected.WeaponGeneration, sequence, action, expected.Pawn.Address,
            expected.Weapon.Address, target, executed, interrupted, boolResult, startedTick, returnedTick,
            reader.U32(command + AfdevBioBotCombatHook.IdleAck), DateTimeOffset.UtcNow);

    private async Task DisarmAsync(CancellationToken token)
    {
        if (!process.IsAlive)
            throw new IOException("战斗命令所属进程已退出。");

        uint before = reader.U32(command + AfdevBioBotCombatHook.IdleAck);
        var elapsed = Stopwatch.StartNew();

        process.WriteMemory(command, [0, 0, 0, 0]);

        while (reader.U32(command + AfdevBioBotCombatHook.IdleAck) == before)
        {
            token.ThrowIfCancellationRequested();

            if (!process.IsAlive || elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("战斗命令未取得原生 IdleAck，必须回收原 Job。");

            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    private bool StillEquipped(AfdevBioBotCombatSnapshot expected)
    {
        uint current = reader.U32(Field(participant.Controller, "Engine.Controller.Pawn"));

        if (current != expected.Pawn.Address || current < 0x10000)
            return false;

        try
        {
            return reader.Identity(current) == expected.Pawn && !Deleted(current) &&
                reader.U32(Field(current, "Engine.Pawn.Weapon")) == expected.Weapon.Address &&
                reader.Identity(expected.Weapon.Address) == expected.Weapon && !Deleted(expected.Weapon.Address) &&
                reader.U32(Field(expected.Weapon.Address, "Engine.Actor.Instigator")) == current;
        }
        catch (InvalidDataException)
        {
            // Normal destruction may unlink UObject/weapon before clearing PC.Pawn. Do not access old equipment during that gap.
            return false;
        }
    }

    private bool TargetAvailable(uint target, uint ownTeam)
    {
        try
        {
            reader.Identity(target);

            if (Deleted(target) || Int(Field(target, "Engine.Pawn.Health", "IntProperty")) <= 0)
                return false;

            uint controller = reader.U32(Field(target, "Engine.Pawn.Controller"));
            uint pri = reader.U32(Field(target, "Engine.Pawn.PlayerReplicationInfo"));
            uint team = reader.U32(Field(pri, "Engine.PlayerReplicationInfo.Team"));

            return controller != 0 && reader.U32(Field(controller, "Engine.Controller.Pawn")) == target &&
                reader.U32(Field(team, "Engine.TeamInfo.TeamIndex", "IntProperty")) != ownTeam;
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            return false;
        }
    }

    private uint Field(uint instance, string path, string kind = "ObjectProperty", uint size = 4) =>
        reader.FieldAddress(instance, path[(path.LastIndexOf('.') + 1)..], path, kind, size);

    private bool Bit(uint instance, string path)
    {
        uint property = reader.FindField(reader.Identity(instance).Class, path[(path.LastIndexOf('.') + 1)..], path);

        return (reader.U32(Field(instance, path, "BoolProperty")) & reader.U32(property + 0x84)) != 0;
    }

    private bool Deleted(uint actor) => (reader.U32(actor + 0x0C) & 0x20000000) != 0 || (reader.U32(actor + 0xB0) & 8) != 0;
    private int Int(uint address) => unchecked((int)reader.U32(address));
    private float Float(uint address) => BitConverter.Int32BitsToSingle(Int(address));

    private float[] Location(uint actor)
    {
        uint address = Field(actor, "Engine.Actor.Location", "StructProperty", 12);
        float[] result = [Float(address), Float(address + 4), Float(address + 8)];

        if (result.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("战斗位置观察含无效浮点值。");

        return result;
    }
}

internal enum AfdevBioBotCombatAction
{
    Aim = 1, LineOfSight = 2, StartFire = 3, StopFire = 4, Reload = 5,
    FaceMovement = 6, FaceTarget = 7, SwitchToBestWeapon = 8, FaceDestination = 9,
    StartSkill = 10, BecomeHero = 11
}

internal sealed record AfdevBioBotCombatSnapshot(QueryObject Pawn, QueryObject Weapon, long PawnGeneration,
    long WeaponGeneration, uint TeamIndex, int Health, float[] Location, float EyeHeight, bool CanAct,
    bool RoundStarted, bool IsMelee, int Ammo, int CarriedAmmo, float WeaponRange, uint ControllerState,
    uint InventoryManager, uint Team, string WeaponClass, string? WeaponState);

internal sealed record AfdevBioBotCombatResult(Guid InstanceId, Guid AttemptId, Guid AllocationId, long WorldGeneration,
    Guid BotId, long PawnGeneration, long WeaponGeneration, uint Ordinal, AfdevBioBotCombatAction Action,
    uint Pawn, uint Weapon, uint TargetPawn, bool NativeExecuted, bool Interrupted, bool? BoolResult,
    uint StartedTick, uint ReturnedTick, uint IdleAck, DateTimeOffset ObservedAtUtc);
