using System.Buffers.Binary;
using System.Diagnostics;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Native navigation-action slot for one ordinary BIO participant. Reuses its page after retirement; normal
/// Pawn replacement stops the old task. Controller/world ownership changes or unknown native completion
/// require reclaiming the original Job.
/// </summary>
internal sealed partial class AfdevBioBotNavigation(AfdevOwnedProcess process, AfdevNativeEvidence world, uint controller,
    uint command, Action<string, object> save, AfdevBioBotMotionRun motion, AfdevBioBotNavigationStop stopping)
{
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly SemaphoreSlim gate = new(1, 1);
    private NavigationExecution? active;
    private QueryObject? previousPawn;
    private long pawnGeneration;
    private long publishedPrograms;
    private uint programSlot;
    private bool poisoned;
    private bool motionStarted;
    private Stopwatch? motionElapsed;
    private AfdevBioBotNavigationState last = new(AfdevBioBotNavigationPhase.Blocked, Guid.Empty, 0, 0, 0, 0,
        false, false, false, 0, 0, 0, null, null, null, 0, 0,
        "等待首个原生导航动作。", DateTimeOffset.UtcNow);

    public async Task<AfdevBioBotNavigationState> StartAsync(uint goal, bool diagnostic, CancellationToken token,
        AfdevBioBotNavigationMode mode = AfdevBioBotNavigationMode.FindPath, AfdevBioBotPoint? point = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        uint candidatePawn = 0;
        uint candidatePawnField = 0;
        NavigationGoal? candidateGoal = null;
        bool publicationAttempted = false;

        try
        {
            CheckWorld();

            if (poisoned)
                return Block("原生身份或命令已有未知完成；必须回收原 Job，禁止重发。", true);
            if (active != null)
                return Block("上次程序尚未安全退休；先取得停止及 IdleAck 收据，禁止覆盖。", true);
            if (point.HasValue != (mode is AfdevBioBotNavigationMode.Point or AfdevBioBotNavigationMode.PointReachability) ||
                point is { } position && (!position.IsValid || goal != 0))
                return Refuse(AfdevBioBotNavigationPhase.TargetInvalid, goal, "坐标动作必须使用独立、有限的坐标目标。");

            if (!point.HasValue)
            {
                try
                {
                    if (goal < 0x10000 ||
                        (!reader.IsA(goal, "Engine.NavigationPoint") && (mode == AfdevBioBotNavigationMode.FindPath || !reader.IsA(goal, "Engine.Pawn"))) ||
                        mode == AfdevBioBotNavigationMode.Pursuit && !reader.IsA(goal, "Engine.Pawn") ||
                        (reader.U32(goal + 0x0C) & 0x20000000) != 0 || (reader.U32(goal + 0xB0) & 8) != 0 ||
                        reader.U32(reader.FieldAddress(goal, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4)) != world.WorldInfo)
                        return Refuse(AfdevBioBotNavigationPhase.TargetInvalid, goal,
                            "目标不是当前世界中有效的导航点或本动作允许的 Pawn；尚未发布程序。");

                    uint targetHealth = 0;
                    uint targetPawnField = 0;

                    if (reader.IsA(goal, "Engine.Pawn"))
                    {
                        uint targetController = reader.U32(reader.FieldAddress(goal, "Controller", "Engine.Pawn.Controller", "ObjectProperty", 4));

                        if (targetController < 0x10000 || targetController == controller)
                            return Refuse(AfdevBioBotNavigationPhase.TargetInvalid, goal, "直接接近目标没有其他有效控制器。");

                        targetHealth = reader.FieldAddress(goal, "Health", "Engine.Pawn.Health", "IntProperty", 4);
                        targetPawnField = reader.FieldAddress(targetController, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4);
                    }

                    candidateGoal = new NavigationGoal(reader.Identity(goal), targetHealth, targetPawnField);

                    if (GoalInvalid(candidateGoal))
                        return Refuse(AfdevBioBotNavigationPhase.TargetInvalid, goal, "目标已死亡或换代，尚未发布程序。");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
                {
                    return Refuse(AfdevBioBotNavigationPhase.TargetInvalid, goal, "目标已失效：" + error.Message);
                }
            }

            uint pawnAddress = reader.FieldAddress(controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4);
            uint pawn = reader.U32(pawnAddress);

            candidatePawn = pawn;
            candidatePawnField = pawnAddress;

            if (pawn < 0x10000)
                return Refuse(AfdevBioBotNavigationPhase.WaitingForPawn, goal, "等待原生出生或换 Pawn 完成。");

            QueryObject identity = reader.Identity(controller);
            QueryObject pawnIdentity = reader.Identity(pawn);
            uint health = reader.FieldAddress(pawn, "Health", "Engine.Pawn.Health", "IntProperty", 4);
            uint pawnController = reader.FieldAddress(pawn, "Controller", "Engine.Pawn.Controller", "ObjectProperty", 4);
            uint pawnWorld = reader.FieldAddress(pawn, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4);

            if (unchecked((int)reader.U32(health)) <= 0 || reader.U32(pawnController) != controller || reader.U32(pawnWorld) != world.WorldInfo)
                return Refuse(AfdevBioBotNavigationPhase.WaitingForPawn, goal, "没有同一世界内存活且被精确持有的 Pawn。");

            uint moveProperty = reader.FindField(world.GameInfoClass, "bCanMove", "UTGame.TGRoundMatch.bCanMove");
            uint moveFlag = reader.FieldAddress(world.GameInfo, "bCanMove", "UTGame.TGRoundMatch.bCanMove", "BoolProperty", 4);
            uint moveMask = reader.U32(moveProperty + 0x84);
            uint roundProperty = reader.FindField(world.GameInfoClass, "bInRound", "UTGame.TGRoundMatch.bInRound");
            uint roundFlag = reader.FieldAddress(world.GameInfo, "bInRound", "UTGame.TGRoundMatch.bInRound", "BoolProperty", 4);
            uint roundMask = reader.U32(roundProperty + 0x84);
            uint roundCount = reader.FieldAddress(world.GameInfo, "RoundCount", "UTGame.TGRoundMatch.RoundCount", "IntProperty", 4);
            uint roundNumber = reader.U32(roundCount);

            if (moveMask == 0 || (moveMask & (moveMask - 1)) != 0 ||
                roundMask == 0 || (roundMask & (roundMask - 1)) != 0)
                throw new InvalidDataException("bCanMove 或 bInRound 不是已反射的单一原生位。");
            if ((reader.U32(moveFlag) & moveMask) == 0 || (reader.U32(roundFlag) & roundMask) == 0)
                return Refuse(AfdevBioBotNavigationPhase.WaitingForMovement, goal, "AFDEV 小局尚未允许移动，不提交寻路程序。");

            AfdevNavigationProgram program = AfdevNavigationProgram.Build(process, world, controller, goal, mode, point);
            uint frame = reader.U32(controller + 0x18);

            if (frame == 0 || reader.U32(frame + 0x14) != controller || reader.U32(frame + 0x18) != 0 || reader.U16(frame + 0x30) != 0)
                return Refuse(AfdevBioBotNavigationPhase.Blocked, goal, "原生状态帧并非空闲，不覆盖已有世界行为。");

            uint node = reader.U32(frame + 0x10), stateNode = reader.U32(frame + 0x28);
            IReadOnlyList<QueryObject> identities = reader.IdentityClosure([controller, pawn, world.GameInfo, node, stateNode, .. program.References]);
            uint pcWorld = reader.FieldAddress(controller, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4);
            uint location = VectorAddress(pawn, "Location", "Core.Object.VectorNoCompress");
            uint velocity = VectorAddress(pawn, "Velocity", "Core.Object.Vector");
            uint acceleration = VectorAddress(pawn, "Acceleration", "Core.Object.Vector");
            uint moveTimer = reader.FieldAddress(controller, "MoveTimer", "Engine.Controller.MoveTimer", "FloatProperty", 4);
            uint moveTarget = reader.FieldAddress(controller, "MoveTarget", "Engine.Controller.MoveTarget", "ObjectProperty", 4);
            byte[] bytes = program.Program;
            uint code = programSlot == 0 ? programSlot = process.AllocateMemory(4096, executable: false) : programSlot;
            byte[] payload = new byte[StateProgramHook.CommandSize];

            void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), value);

            Set(0x40, world.World); Set(0x44, world.WorldInfo); Set(0x48, controller); Set(0x4C, identity.Class);
            Set(0x50, identity.Index); Set(0x54, identity.NameIndex); Set(0x58, identity.NameNumber);
            Set(0x5C, frame); Set(0x60, node); Set(0x64, stateNode); Set(0x68, code); Set(0x6C, code + program.StopOffset);
            Set(0x70, process.MainThreadId); Set(0x74, reader.U32(0x02063820)); Set(0x78, world.GameInfo);
            Set(0x7C, program.LatentIndex); Set(0x80, pcWorld - controller); Set(0x84, (uint)identities.Count);
            Set(0x88, 0); Set(0x8C, pawn); Set(0x90, pawnAddress); Set(0x94, pawnController);
            Set(0x98, pawnWorld); Set(0x9C, health);

            int cursor = 0x100;

            foreach (QueryObject item in identities)
            {
                foreach (uint value in new[] { item.Address, item.Index, item.Outer, item.NameIndex, item.NameNumber, item.Class })
                {
                    Set(cursor, value);
                    cursor += 4;
                }
            }

            await ResourceCallRun.DisarmAndWaitAsync(process, command, token).ConfigureAwait(false);
            program.ValidateBinding(process, world, controller);
            process.WriteMemory(code, bytes);

            Guid taskId = Guid.NewGuid();

            await stopping.PrepareAsync(taskId, frame, code, program, token).ConfigureAwait(false);
            process.WriteMemory(command + 4, payload.AsSpan(4).ToArray());

            if (!process.ReadMemory(code, bytes.Length).AsSpan().SequenceEqual(bytes) ||
                !process.ReadMemory(command, payload.Length).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("原生导航未发布页的回读不一致。");

            if (previousPawn != pawnIdentity)
            {
                pawnGeneration++;
                previousPawn = pawnIdentity;
            }

            active = new NavigationExecution(taskId, identity, pawnIdentity, identities, goal, frame, node, stateNode, code, code + program.StopOffset,
                pawnAddress, pawnController, pawnWorld, health, location, velocity, acceleration, moveTimer, moveTarget,
                moveFlag, moveMask, roundFlag, roundMask, roundCount, roundNumber, ReadVector(location), Stopwatch.StartNew(),
                candidateGoal, mode, point, program.LatentIndex);

            last = new AfdevBioBotNavigationState(AfdevBioBotNavigationPhase.Running, taskId, pawnGeneration,
                controller, pawn, goal, true, false, false, 0, 0, 0, null, null, null, 0, 0,
                "固定槽中的原生动作已准备。", DateTimeOffset.UtcNow) { Point = point };

            save("navigation-prepared", new StartAsyncJson1
            {
                Id = active.Id, PawnGeneration = pawnGeneration, InstanceId = world.InstanceId, AttemptId = world.AttemptId, AllocationId = world.AllocationId,
                WorldGeneration = world.WorldGeneration, Controller = identity, Pawn = pawnIdentity, goal = goal, frame = frame, code = code,
                ProgramHex = Convert.ToHexString(bytes), ProgramSha256 = TickHook.Hash(bytes), EvidenceId = AfdevNavigationProgram.EvidenceId,
                mode = mode, point = point, StopOffset = program.StopOffset, LatentIndex = program.LatentIndex,
                Identities = identities, DiagnosticOnly = diagnostic, MoveMask = moveMask, MoveWord = reader.U32(moveFlag),
                ProgramSlotCount = 1, PublishedPrograms = publishedPrograms + 1, RoundCount = roundNumber
            });
            token.ThrowIfCancellationRequested();

            if (RoundPaused(active))
                throw new AfdevBioBotActionInterruptedException("导航准备期间原生小局已切换，未发布旧回合动作。");

            publicationAttempted = true;
            process.WriteMemory(command, [1, 0, 0, 0]);
            publishedPrograms++;

            return ReadState();
        }
        catch (AfdevBioBotActionInterruptedException)
        {
            // Frame-generation change during preparation published no native command; wait for the next native-ready state.
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Callers must still use StopAsync. Unpublished slots and published actions share the same retirement path.
            throw;
        }
        catch (Exception) when (!publicationAttempted && candidatePawnField != 0 && process.IsAlive &&
            reader.U32(candidatePawnField) != candidatePawn)
        {
            throw new AfdevBioBotActionInterruptedException("导航准备期间 Pawn 已更换，未发布旧动作。");
        }
        catch (Exception) when (!publicationAttempted && candidateGoal != null && process.IsAlive && GoalInvalid(candidateGoal))
        {
            throw new AfdevBioBotActionInterruptedException("导航准备期间目标已更换，未发布旧动作。");
        }
        catch (Exception error)
        {
            poisoned = true;
            last = last with { Phase = AfdevBioBotNavigationPhase.Blocked, RequiresProcessStop = true,
                Reason = "导航准备或发布失败，要求宿主回收原 Job：" + error.Message, ObservedAtUtc = DateTimeOffset.UtcNow };

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AfdevBioBotNavigationState> CheckAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            return active == null ? last : ReadState();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartMotionAsync(CancellationToken token, bool observeOnly = false)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            NavigationExecution item = active ?? throw new InvalidOperationException("物理执行需要已发布的原生导航任务。");

            AfdevBioBotNavigationState state = ReadState();

            if (motionStarted || poisoned)
                throw new InvalidOperationException("导航任务不能重复启用物理执行，或已经失效。");
            if (item.Mode is AfdevBioBotNavigationMode.DirectReachability or AfdevBioBotNavigationMode.PointReachability)
                throw new InvalidOperationException("直接可达查询不能启用物理执行。");
            if (item.CleanCompletion || state.Phase is AfdevBioBotNavigationPhase.PawnChanged
                or AfdevBioBotNavigationPhase.WaitingForMovement or AfdevBioBotNavigationPhase.TargetInvalid)
                return;

            await motion.StartAsync(controller, item.Pawn.Address, item.StopCode, pawnGeneration, token, observeOnly,
                item.LatentIndex).ConfigureAwait(false);
            motionStarted = true;
            motionElapsed = observeOnly ? null : Stopwatch.StartNew();
            item.ProgressLocation = ReadVector(item.Location);
            item.LastProgressAt = TimeSpan.Zero;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EnablePhysicsAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            ReadState();

            if (!motionStarted || motionElapsed != null || poisoned)
                throw new InvalidOperationException("当前导航不处于可启用的只观察阶段。");

            await motion.EnablePhysicsAsync(token).ConfigureAwait(false);
            motionElapsed = Stopwatch.StartNew();
            active!.ProgressLocation = ReadVector(active.Location);
            active.LastProgressAt = TimeSpan.Zero;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops observation/physics and obtains a PC-only native cancellation receipt without reading old
    /// Pawns or writing world fields.
    /// </summary>
    public async Task<AfdevBioBotNavigationState> StopAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (!process.IsAlive)
            {
                active = null;
                last = last with { Phase = AfdevBioBotNavigationPhase.Stopped, Active = false, RequiresProcessStop = false,
                    Reason = "拥有的原生进程已停止，全部私有页随 Job 回收。", ObservedAtUtc = DateTimeOffset.UtcNow };
                return last;
            }

            NavigationExecution? retiring = active;

            // Disable the old-Pawn-dependent state observer before stopping physics; cancellation uses an independent PC-only descriptor.
            await ResourceCallRun.DisarmAndWaitAsync(process, command, token).ConfigureAwait(false);

            if (motionStarted)
            {
                await motion.StopDrivingAsync(token).ConfigureAwait(false);
                motionStarted = false;
                motionElapsed = null;
            }

            if (retiring == null || reader.U32(command + 4) == 0)
            {
                active = null;
                last = last with { Phase = AfdevBioBotNavigationPhase.Stopped, Active = false, RequiresProcessStop = poisoned,
                    Reason = "原生 IdleAck 已确认，未安装任何程序。", ObservedAtUtc = DateTimeOffset.UtcNow };
                return retiring == null ? last : RecordRetirement(retiring, null);
            }

            AfdevBioBotProgramRetirement receipt = await stopping.RetireAsync(token).ConfigureAwait(false);

            active = null;
            last = last with { Phase = AfdevBioBotNavigationPhase.Stopped, Active = false,
                RequiresProcessStop = poisoned, Reason = receipt.NativeStateChanged
                    ? "原生状态已脱离旧程序，保留新状态执行；旧动作槽可安全复用。"
                    : "原控制器的自有 Code/latent 已由原生清除，动作槽可安全复用。" };
            return RecordRetirement(retiring, receipt);
        }
        finally
        {
            gate.Release();
        }
    }

    private AfdevBioBotNavigationState ReadState()
    {
        NavigationExecution item = active ?? throw new InvalidOperationException("没有导航诊断任务。");

        try
        {
            CheckWorld();

            if (reader.Identity(controller) != item.Controller)
                throw new InvalidDataException("导航控制器的精确身份已经改变。");

            // Check current possession before reading the old Pawn. Normal death/transformation ends only this action, not the entire instance.
            if (reader.U32(item.PawnAddress) != item.Pawn.Address)
                return Interrupt(AfdevBioBotNavigationPhase.PawnChanged, "原生 Pawn 已更换，等待旧动作退休。");
            if (reader.Identity(item.Pawn.Address) != item.Pawn)
                return Interrupt(AfdevBioBotNavigationPhase.PawnChanged, "原生 Pawn 对象已换代，等待旧动作退休。");
            if (unchecked((int)reader.U32(item.Health)) <= 0 || reader.U32(item.PawnController) != controller ||
                (reader.U32(item.Pawn.Address + 0xB0) & 8) != 0)
                return Interrupt(AfdevBioBotNavigationPhase.PawnChanged, "原生 Pawn 死亡或解除占有，等待旧动作退休。");
            if (GoalInvalid(item.Target))
                return Interrupt(AfdevBioBotNavigationPhase.TargetInvalid, "目标已死亡、删除或换代，先退休旧动作。");
            if (reader.U32(item.PawnWorld) != world.WorldInfo ||
                reader.U32(controller + 0x18) != item.Frame || item.Identities.Any(value => reader.Identity(value.Address) != value))
                throw new InvalidDataException("导航失去精确活 Pawn 代际、状态帧或引用身份。");
            if (NativeStateChanged(item))
                return Interrupt(AfdevBioBotNavigationPhase.WaitingForMovement, "原生控制器已切换状态，先退休旧导航并保留新状态执行。");
            if (RoundPaused(item))
                return Interrupt(AfdevBioBotNavigationPhase.WaitingForMovement, "原生小局暂停或换代，先退休当前动作。");

            byte[] bytes = process.ReadMemory(command, 0x30);

            uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

            float[] location = ReadVector(item.Location);
            float[] velocity = ReadVector(item.Velocity);
            float[] acceleration = ReadVector(item.Acceleration);
            bool moved = location.Where((value, index) => Math.Abs(value - item.InitialLocation[index]) > 0.01f).Any();
            bool complete = Word(8) == 1;
            bool clean = complete && Word(4) == 1 && Word(12) == 2 && Word(28) == 0 && Word(36) == 0 &&
                reader.U32(item.Frame + 0x18) == 0 && reader.U16(item.Frame + 0x30) == 0;
            uint moveTarget = reader.U32(item.MoveTarget);

            // Jitter below one world unit does not reset stall timing. This recovery-scheduling threshold is not native arrival evidence.
            if (motionElapsed != null && location.Where((value, index) => Math.Abs(value - item.ProgressLocation[index]) >= 1).Any())
            {
                item.ProgressLocation = location;
                item.LastProgressAt = motionElapsed.Elapsed;
            }

            bool stalled = motionElapsed != null && motionElapsed.Elapsed - item.LastProgressAt > TimeSpan.FromSeconds(2) && Word(40) != 0;
            bool expired = item.Elapsed.Elapsed > TimeSpan.FromSeconds(20);
            // MoveTo normally clears MoveTarget. Coordinate actions must not inherit Actor actions' null-target failure condition.
            bool noPath = clean && Word(40) == 0 && moveTarget == 0 && item.Mode != AfdevBioBotNavigationMode.Point;
            string reason = noPath ? "原生寻路或直接可达检查没有返回移动目标；这不是到达。"
                : clean && item.Mode is AfdevBioBotNavigationMode.DirectReachability or AfdevBioBotNavigationMode.PointReachability
                    ? "直接可达查询已完成；未启用物理，也不表示已经到达。"
                : clean ? "原生程序已完成；到达必须由独立的原生到达查询确认。"
                : complete ? "原生门拒绝或身份发生变化，禁止重发。"
                : expired ? "本段已达到二十秒期限；需同代原生取消与退休收据后才能重规划。"
                : stalled ? "活动物理已连续两秒没有有效位移；需同代原生取消与退休收据后才能重规划。"
                : "仅观察原生程序执行；不把 latent 或位置变化当作到达。";
            AfdevBioBotNavigationPhase phase = noPath ? AfdevBioBotNavigationPhase.NoPath
                : clean ? AfdevBioBotNavigationPhase.Completed
                : complete ? AfdevBioBotNavigationPhase.Blocked
                : expired ? AfdevBioBotNavigationPhase.TimedOut
                : stalled ? AfdevBioBotNavigationPhase.Stalled
                : AfdevBioBotNavigationPhase.Running;

            // The state gate may latch rejection during sampling. Reconfirm migration rather than depend on death/Pawn-replacement ordering.
            if (complete && !clean && (NativeStateChanged(item) || RoundPaused(item)))
                return Interrupt(AfdevBioBotNavigationPhase.WaitingForMovement, "采样期间原生状态或小局已迁移，等待旧动作退休。");
            if (complete && !clean && GoalInvalid(item.Target))
                return Interrupt(AfdevBioBotNavigationPhase.TargetInvalid, "采样期间目标已失效，等待旧动作退休。");

            if (motionStarted)
            {
                AfdevBioBotMotionObservation observation = motion.Read();

                save("motion-state", observation);

                if (observation.Status == 3)
                {
                    if (reader.U32(item.PawnAddress) != item.Pawn.Address || unchecked((int)reader.U32(item.Health)) <= 0)
                        return Interrupt(AfdevBioBotNavigationPhase.PawnChanged, "物理门已停用旧 Pawn，等待动作退休。");
                    if (NativeStateChanged(item) || RoundPaused(item))
                        return Interrupt(AfdevBioBotNavigationPhase.WaitingForMovement, "物理门已观察到原生状态或小局迁移，等待旧动作退休。");

                    poisoned = true;
                    return Block("同代原生物理门拒绝，必须回收原 Job。", true);
                }
            }

            item.CleanCompletion = clean;
            poisoned |= complete && !clean;
            last = new AfdevBioBotNavigationState(phase,
                item.Id, pawnGeneration, controller, item.Pawn.Address, item.Goal, !clean, poisoned, moved,
                Word(40), Word(36), moveTarget, location, velocity, acceleration,
                ReadFiniteFloat(item.MoveTimer), reader.U32(controller + 0x400), reason, DateTimeOffset.UtcNow) { Point = item.Point };

            save("navigation-state", last);

            return last;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            if (process.IsAlive && reader.U32(item.PawnAddress) != item.Pawn.Address)
                return Interrupt(AfdevBioBotNavigationPhase.PawnChanged, "采样期间 Pawn 已更换，等待旧动作退休。");
            if (process.IsAlive && GoalInvalid(item.Target))
                return Interrupt(AfdevBioBotNavigationPhase.TargetInvalid, "采样期间目标已换代，等待旧动作退休。");

            poisoned = true;
            return Block(error.Message, true);
        }
    }

    private bool GoalInvalid(NavigationGoal? target)
    {
        if (target == null)
            return false;

        try
        {
            uint goal = target.Identity.Address;

            return reader.Identity(goal) != target.Identity || (reader.U32(goal + 0x0C) & 0x20000000) != 0 ||
                (reader.U32(goal + 0xB0) & 8) != 0 ||
                target.Health != 0 && (unchecked((int)reader.U32(target.Health)) <= 0 || reader.U32(target.PawnField) != goal);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private bool NativeStateChanged(NavigationExecution item)
    {
        if (reader.U32(controller + 0x18) != item.Frame)
            throw new InvalidDataException("导航控制器的状态帧身份已经改变。");

        return reader.U32(item.Frame + 0x10) != item.Node || reader.U32(item.Frame + 0x28) != item.StateNode;
    }

    private bool RoundPaused(NavigationExecution item) =>
        (reader.U32(item.CanMove) & item.CanMoveMask) == 0 ||
        (reader.U32(item.InRound) & item.InRoundMask) == 0 || reader.U32(item.RoundCount) != item.RoundNumber;

    private AfdevBioBotNavigationState Interrupt(AfdevBioBotNavigationPhase phase, string reason)
    {
        last = last with { Phase = phase, Reason = reason, ObservedAtUtc = DateTimeOffset.UtcNow };
        save("navigation-state", last);
        return last;
    }

    private AfdevBioBotNavigationState Block(string reason, bool requiresProcessStop)
    {
        last = last with { Phase = AfdevBioBotNavigationPhase.Blocked, RequiresProcessStop = requiresProcessStop,
            Reason = reason, ObservedAtUtc = DateTimeOffset.UtcNow };
        save("navigation-state", last);
        return last;
    }

    private AfdevBioBotNavigationState Refuse(AfdevBioBotNavigationPhase phase, uint goal, string reason)
    {
        last = new AfdevBioBotNavigationState(phase, Guid.Empty, pawnGeneration, controller, 0, goal,
            false, false, false, 0, 0, 0, null, null, null, 0, 0, reason, DateTimeOffset.UtcNow);
        save("navigation-state", last);
        return last;
    }

    private AfdevBioBotNavigationState RecordRetirement(NavigationExecution item, AfdevBioBotProgramRetirement? cancellation)
    {
        var receipt = new AfdevBioBotNavigationRetirement(item.Id, item.Pawn.Address, pawnGeneration,
            cancellation?.CallsStarted ?? 0, cancellation?.CallsReturned ?? 0,
            reader.U32(command + 8), reader.U32(command + 12), true, DateTimeOffset.UtcNow)
        {
            ControllerStateCleared = cancellation?.Status == 1,
            NativeStateChanged = cancellation?.NativeStateChanged == true,
            ProgramSlot = programSlot,
            PhysicsObserverSlot = motion.ObserverSlot,
            PublishedPrograms = publishedPrograms
        };

        last = last with { Retirement = receipt, ObservedAtUtc = receipt.RetiredAtUtc };
        save("navigation-retired", receipt);
        return last;
    }

    private void CheckWorld()
    {
        if (!process.IsAlive || process.ProcessId != world.ProcessId || process.CreationTimeUtc != world.ProcessCreationTimeUtc ||
            world.InstanceId == Guid.Empty || world.AttemptId == Guid.Empty || world.AllocationId == Guid.Empty ||
            !AfdevMapCatalog.MatchesBio(world) || !AfdevBotNativeImage.IsVerifiedFor(process, world.AfdevSha256) ||
            reader.U32(0x02066BF8) != world.World || reader.Identity(world.GameInfo).Class != world.GameInfoClass ||
            reader.U32(world.WorldInfo + 0x414) != world.GameInfo)
            throw new InvalidDataException("导航已失去原物理进程、allocation 或世界绑定。");
    }

    private uint VectorAddress(uint pawn, string name, string expectedType)
    {
        uint property = reader.FindField(reader.U32(pawn + 0x34), name, "Engine.Actor." + name);
        uint address = reader.FieldAddress(pawn, name, "Engine.Actor." + name, "StructProperty", 12);
        uint type = reader.U32(property + 0x84);
        QueryObject identity = reader.Identity(type);

        if (reader.Path(type) != expectedType || reader.Path(identity.Class) != "Core.ScriptStruct" || reader.U32(type + 0x50) != 12)
            throw new InvalidDataException("导航向量类型与当前包不符。");

        string[] names = ["X", "Y", "Z"];

        for (int i = 0; i < names.Length; i++)
        {
            uint field = reader.FindField(type, names[i], expectedType + "." + names[i]);
            QueryObject axis = reader.Identity(field);

            if (axis.Outer != type || reader.Path(axis.Class) != "Core.FloatProperty" || reader.U32(field + 0x40) != 1 ||
                reader.U32(field + 0x44) != 4 || reader.U32(field + 0x60) != i * 4)
                throw new InvalidDataException("导航向量 XYZ 元素布局与当前包不符。");
        }

        return address;
    }

    private float ReadFiniteFloat(uint address)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(address, 4));

        return float.IsFinite(value) ? value : throw new InvalidDataException("导航浮点诊断值不是有限数。");
    }

    private float[] ReadVector(uint address)
    {
        byte[] bytes = process.ReadMemory(address, 12);
        float[] values = Enumerable.Range(0, 3).Select(index => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * 4, 4))).ToArray();

        if (values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("导航向量包含非有限值。");

        return values;
    }

    private sealed record NavigationExecution(Guid Id, QueryObject Controller, QueryObject Pawn,
        IReadOnlyList<QueryObject> Identities, uint Goal, uint Frame, uint Node, uint StateNode, uint Code, uint StopCode, uint PawnAddress, uint PawnController,
        uint PawnWorld, uint Health, uint Location, uint Velocity, uint Acceleration, uint MoveTimer, uint MoveTarget,
        uint CanMove, uint CanMoveMask, uint InRound, uint InRoundMask, uint RoundCount, uint RoundNumber,
        float[] InitialLocation, Stopwatch Elapsed, NavigationGoal? Target, AfdevBioBotNavigationMode Mode,
        AfdevBioBotPoint? Point, uint LatentIndex)
    {
        public bool CleanCompletion { get; set; }
        public float[] ProgressLocation { get; set; } = InitialLocation;
        public TimeSpan LastProgressAt { get; set; }
    }

    private sealed record NavigationGoal(QueryObject Identity, uint Health, uint PawnField);
}

/// <summary>
/// Progress of one native segment. Completion or cancellation does not establish final-target arrival.
/// </summary>
public enum AfdevBioBotNavigationPhase
{
    Blocked,
    Running,
    Stopped,
    Completed,
    NoPath,
    WaitingForMovement,
    Stalled,
    TimedOut,
    TargetInvalid,
    BudgetExhausted,
    WaitingForPawn,
    PawnChanged
}

/// <summary>
/// State and motion evidence for the same task. ControllerRaw400Bits contains raw reflected ServerTimeStamp
/// bits.
/// </summary>
public sealed record AfdevBioBotNavigationState(AfdevBioBotNavigationPhase Phase, Guid TaskId, long PawnGeneration,
    uint Controller, uint Pawn, uint Goal, bool Active, bool RequiresProcessStop, bool PositionChanged,
    uint LatentObservations, uint LatentAction, uint MoveTarget, float[]? Location, float[]? Velocity, float[]? Acceleration,
    float MoveTimer, uint ControllerRaw400Bits, string Reason, DateTimeOffset ObservedAtUtc)
{
    public AfdevBioBotNavigationRetirement? Retirement { get; init; }
    public AfdevBioBotPoint? Point { get; init; }
}

/// <summary>
/// Independent retirement receipt preserving latched state-program results. Successful cancellation does
/// not mean arrival.
/// </summary>
public sealed record AfdevBioBotNavigationRetirement(Guid TaskId, uint Pawn, long PawnGeneration,
    uint CancelStarted, uint CancelReturned, uint StateProgramCompleted, uint StateProgramStatus,
    bool IdleAcknowledged, DateTimeOffset RetiredAtUtc)
{
    /// <summary>
    /// The original state frame no longer references the owned program. Newly selected native state need
    /// not be cleared.
    /// </summary>
    public bool ControllerStateCleared { get; init; }
    public bool NativeStateChanged { get; init; }
    public uint ProgramSlot { get; init; }
    public uint PhysicsObserverSlot { get; init; }
    public long PublishedPrograms { get; init; }
    public bool NativeCancellation => CancelStarted == 1 && CancelReturned == 1 &&
        ControllerStateCleared && IdleAcknowledged;
}
