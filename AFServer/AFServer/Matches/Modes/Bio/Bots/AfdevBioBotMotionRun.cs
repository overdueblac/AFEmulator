using System.Buffers.Binary;
using System.Diagnostics;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Independent physics-scheduling slot in the same native instance. Reuse requires observation retirement
/// followed by IdleAck.
/// </summary>
internal sealed partial class AfdevBioBotMotionRun(AfdevOwnedProcess process, AfdevNativeEvidence world, uint command,
    uint trace, uint factoryCommand, Action<string, object> save, AfdevBioBotPhysicsObservers observers)
{
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool published;
    private bool poisoned;
    private uint observerSlot;
    private long lastGeneration;
    private QueryObject? lastPawn;

    public uint ObserverSlot => observerSlot;

    public async Task StartAsync(uint controller, uint pawn, uint privateStop, long generation, CancellationToken token,
        bool observeOnly = false, uint expectedLatent = 503)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (published || poisoned || generation <= 0 || reader.U32(factoryCommand + 0x30) != controller)
                throw new InvalidOperationException("运动实验已发布或代际无效；不得重用旧任务。");

            await DisarmAsync(token).ConfigureAwait(false);

            uint pawnField = reader.FieldAddress(controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4);

            if (reader.U32(pawnField) != pawn)
                throw new AfdevBioBotActionInterruptedException("物理准备前 Pawn 已更换。");

            var functions = new AfdevBioBotMotionFunctions(process);
            ResourceFunction physics = functions.DescribeAutonomousPhysics(pawn);
            ResourceFunction stop = functions.DescribeStopLatentExecution(controller);
            uint frame = reader.U32(controller + 0x18);

            if (frame == 0)
                throw new InvalidDataException("运动实验缺少原生 StateFrame。");

            uint node = reader.U32(frame + 0x10);
            uint state = reader.U32(frame + 0x28);
            uint pri = reader.U32(reader.FieldAddress(controller, "PlayerReplicationInfo", "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4));
            QueryObject pawnIdentity = reader.Identity(pawn);
            AfdevBioBotPhysicsObserver observer = observers.Resolve(command, pawnIdentity);
            uint vtable = observer.Vtable;
            uint original = observer.Original;
            byte[] payload = new byte[AfdevBioBotMotionHook.CommandSize];
            List<(uint Address, uint Value, uint Mask)> guards = [];
            List<uint> roots = [controller, pawn, pri, world.GameInfo, node, state, physics.Function.Address, stop.Function.Address];

            if (generation < lastGeneration || (generation == lastGeneration && lastPawn != pawnIdentity)
                || reader.U32(frame + 0x14) != controller || reader.U32(frame + 0x18) != privateStop
                || expectedLatent is not (501 or 503) || reader.U16(frame + 0x30) != expectedLatent || privateStop < 0x10000
                || process.ReadMemory(privateStop, 1)[0] != 8 || original < 0x00400000 || original >= 0x03000000
                || vtable < 0x00400000 || vtable >= 0x03000000 - 0x230)
                throw new InvalidDataException("物理执行要求同一代 Pawn 的冻结 MoveTo/MoveToward 和私有 Stop。");

            void Set(uint offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan((int)offset, 4), value);
            void Guard(uint address, uint value, uint mask = uint.MaxValue) => guards.Add((address, value, mask));
            uint Field(uint receiver, string name, string path, string type, uint size = 4)
            {
                uint property = reader.FindField(reader.Identity(receiver).Class, name, path);
                uint address = reader.FieldAddress(receiver, name, path, type, size);

                roots.Add(property);
                Guard(property + 0x60, address - receiver);
                Guard(property + 0x44, size);
                return address;
            }

            void Method(ResourceFunction method)
            {
                uint f = method.Function.Address;

                Guard(f + 0xAC, method.Flags); Guard(f + 0xB0, method.NativeIndex, 0xFFFF);
                Guard(f + 0xBE, method.ParmsSize, 0xFFFF); Guard(f + 0xC0, 0xFFFF, 0xFFFF);
                Guard(f + 0xC8, method.ExecPointer); Guard(f + 0x54, method.ScriptPointer);
                Guard(f + 0x4C, reader.U32(f + 0x4C)); Guard(f + 0x5C, method.ScriptCapacity);
                Guard(f + 0x58, method.ScriptCount); Guard(f + 0x50, method.PropertiesSize);
                for (uint at = 0; at < method.ScriptCount; at += 4)
                {
                    uint remaining = Math.Min(4u, method.ScriptCount - at);
                    uint mask = remaining == 4 ? uint.MaxValue : (1u << ((int)remaining * 8)) - 1;

                    Guard(method.ScriptPointer + at, reader.U32(method.ScriptPointer + at) & mask, mask);
                }
                Guard(reader.U32(method.Receiver.Address) + 0x120, QueryHook.ActorProcessEvent);
                foreach (ResourceParameter parameter in method.Parameters)
                {
                    roots.Add(parameter.Identity.Address);
                    Guard(parameter.Identity.Address + 0x3C, reader.U32(parameter.Identity.Address + 0x3C));
                    Guard(parameter.Identity.Address + 0x40, 1);
                    Guard(parameter.Identity.Address + 0x4C, 0);
                    Guard(parameter.Identity.Address + 0x60, parameter.Offset);
                    Guard(parameter.Identity.Address + 0x44, parameter.Size);
                    Guard(parameter.Identity.Address + 0x48, parameter.Flags);
                }
            }

            Set(0x40, world.World); Set(0x44, world.WorldInfo); Set(0x48, controller); Set(0x4C, pawn);
            Set(0x50, frame); Set(0x54, node); Set(0x58, state); Set(0x5C, privateStop);
            Set(0x60, process.MainThreadId); Set(0x64, reader.U32(0x02063820)); Set(0x68, world.GameInfo);
            Set(0x7C, Field(controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty"));
            Set(0x80, Field(pawn, "Controller", "Engine.Pawn.Controller", "ObjectProperty"));
            Set(0x84, Field(pawn, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty"));
            Set(0x88, Field(pawn, "Health", "Engine.Pawn.Health", "IntProperty"));
            Set(0x8C, Field(controller, "Player", "Engine.PlayerController.Player", "ObjectProperty"));
            Set(0x90, Field(pri, "bBot", "Engine.PlayerReplicationInfo.bBot", "BoolProperty"));

            uint botProperty = reader.FindField(reader.Identity(pri).Class, "bBot", "Engine.PlayerReplicationInfo.bBot");
            uint botMask = reader.U32(botProperty + 0x84);
            uint canMove = Field(world.GameInfo, "bCanMove", "UTGame.TGRoundMatch.bCanMove", "BoolProperty");
            uint canMoveProperty = reader.FindField(world.GameInfoClass, "bCanMove", "UTGame.TGRoundMatch.bCanMove");
            uint canMoveMask = reader.U32(canMoveProperty + 0x84);

            if (botMask == 0 || (botMask & (botMask - 1)) != 0 || canMoveMask == 0 || (canMoveMask & (canMoveMask - 1)) != 0)
                throw new InvalidDataException("Bot 或移动门布尔布局无效。");

            Set(0x94, botMask); Set(0x74, canMove); Set(0x78, canMoveMask);
            Guard(botProperty + 0x84, botMask); Guard(canMoveProperty + 0x84, canMoveMask);
            Set(0x98, physics.Function.Address); Set(0x9C, stop.Function.Address);
            Set(0xA0, vtable); Set(0xA4, original); Set(0xA8, observer.Entry);
            Set(0xB4, pawnIdentity.Index); Set(0xB8, pawnIdentity.Class);
            Set(AfdevBioBotMotionHook.PawnOuter, pawnIdentity.Outer);
            Set(AfdevBioBotMotionHook.PawnNameIndex, pawnIdentity.NameIndex);
            Set(AfdevBioBotMotionHook.PawnNameNumber, pawnIdentity.NameNumber);
            Set(AfdevBioBotMotionHook.ExpectedLatent, expectedLatent);
            Set(AfdevBioBotMovementInput.Acceleration, Field(pawn, "Acceleration", "Engine.Actor.Acceleration", "StructProperty", 12));
            Set(AfdevBioBotMovementInput.MaxForwardAccel, Field(pawn, "MaxForwardAccel", "UTGame.TGPawn.MaxForwardAccel", "FloatProperty"));
            Set(AfdevBioBotMovementInput.MovingScale, Field(pawn, "fMovingScale", "UTGame.TGPawn.fMovingScale", "FloatProperty"));
            Set(AfdevBioBotMovementInput.Physics, Field(pawn, "Physics", "Engine.Actor.Physics", "ByteProperty", 1));
            Set(AfdevBioBotMovementInput.IgnoreMoveInput, Field(controller, "bIgnoreMoveInput", "Engine.PlayerController.bIgnoreMoveInput", "ByteProperty", 1));
            Method(physics); Method(stop);
            Guard(controller + 0x18, frame); Guard(privateStop, 8, 0xFF);
            Guard(Field(controller, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty"), world.WorldInfo);
            Guard(Field(pawn, "CustomTimeDilation", "Engine.Actor.CustomTimeDilation", "FloatProperty"), 0x3F800000);
            Guard(Field(controller, "PlayerReplicationInfo", "Engine.Controller.PlayerReplicationInfo", "ObjectProperty"), pri);

            IReadOnlyList<QueryObject> identities = IdentityClosure(roots);

            if (guards.Count > 80)
                throw new InvalidDataException("运动元数据断言超过固定上限。");

            save("motion-descriptor-prepared", new StartAsyncJson1 { Generation = generation, IdentityCount = identities.Count,
                GuardCount = guards.Count, Guards = guards.Select(g => new StartAsyncJson2 { Address = g.Address, Value = g.Value, Mask = g.Mask }).ToArray() });
            Set(0x6C, (uint)identities.Count); Set(0x70, (uint)guards.Count);

            int cursor = 0x200;

            foreach (QueryObject identity in identities)
            {
                foreach (uint value in new[] { identity.Address, identity.Index, identity.Outer, identity.NameIndex, identity.NameNumber, identity.Class })
                {
                    Set((uint)cursor, value); cursor += 4;
                }
            }

            cursor = 0x900;
            foreach (var guard in guards)
            {
                Set((uint)cursor, guard.Address); Set((uint)cursor + 4, guard.Value); Set((uint)cursor + 8, guard.Mask);
                cursor += 12;
            }

            observers.Publish(command, pawnIdentity, observer, payload);

            observerSlot = observer.Entry;
            lastGeneration = generation;
            lastPawn = pawnIdentity;
            save("motion-installed", new StartAsyncJson3 { Generation = generation, Pawn = pawnIdentity, OriginalPhysics = original,
                OriginalPhysicsPrefix = Convert.ToHexString(process.ReadMemory(original, 128)), Observer = observer.Entry,
                ObserverHex = Convert.ToHexString(observer.Code), SharedObserver = true,
                Trace = trace, BindingTick = reader.U32(command + AfdevBioBotMotionHook.BindingTick),
                ObserverRetainedUntilOwnedJobStops = true, Physics = physics, Stop = stop, Guards = guards.Count,
                PhysicsEntry = AfdevBioBotMotionFunctions.AutonomousPhysicsBody,
                PhysicsBodyBytes = AfdevBioBotMotionFunctions.AutonomousPhysicsBodyLength,
                PhysicsBodySha256 = AfdevBioBotMotionFunctions.AutonomousPhysicsBodyHash,
                PhysicsCallingConvention = "ECX=bound Pawn; stack=float World.Tick delta; native ret 4",
                IndexedPhysicsRejectedByProcessEvent = true,
                PhysicsDispatchReturn = AfdevBioBotMotionFunctions.AutonomousPhysicsDispatchReturn,
                PhysicsReturnsObservedAtVirtualSlot = true,
                MovementInput = "沿原生导航方向，使用 TGPlayerController 的 MaxForwardAccel × fMovingScale；不改速度或物理 delta。" });
            token.ThrowIfCancellationRequested();
            published = true;
            process.WriteMemory(command, [observeOnly ? (byte)3 : (byte)1, 0, 0, 0]);
        }
        catch (Exception failure)
        {
            poisoned = true;
            try
            {
                // Caller cancellation cannot prevent stopping physics publication. Unknown return still requires original-host cleanup.
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
                observers.Retire(command);
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stoppingFailure)
            {
                throw new AggregateException("运动命令失败且未取得停止确认；必须回收原 Job。", failure, stoppingFailure);
            }

            if (!published && process.IsAlive &&
                (failure is OperationCanceledException or AfdevBioBotActionInterruptedException ||
                 reader.U32(reader.FieldAddress(controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4)) != pawn ||
                 reader.U32(controller + 0x18) is uint currentFrame && currentFrame != 0 &&
                 reader.U32(currentFrame + 0x18) == 0 && reader.U16(currentFrame + 0x30) == 0))
            {
                poisoned = false;

                if (failure is OperationCanceledException)
                    throw;

                throw new AfdevBioBotActionInterruptedException("物理准备期间原生动作结束或 Pawn 更换，未发物理请求。");
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }



    /// <summary>
    /// Switches from the same-generation observation baseline to native physics while retaining actual call
    /// counts, without rebuilding/changing world objects.
    /// </summary>
    public async Task EnablePhysicsAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (!published || poisoned || reader.U32(command) != 3 || reader.U32(command + 4) != 0)
                throw new InvalidOperationException("原生物理只能从同代有效观察任务启用一次。");

            await DisarmAsync(token).ConfigureAwait(false);
            if (reader.U32(command + 4) != 0)
                throw new InvalidDataException("观察任务在启用交接期间已结束或失去绑定。");

            save("motion-before-enable", Read());
            token.ThrowIfCancellationRequested();
            process.WriteMemory(command, [1, 0, 0, 0]);
        }
        catch (Exception failure)
        {
            poisoned = true;
            try
            {
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
                observers.Retire(command);
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stoppingFailure)
            {
                throw new AggregateException("观察任务启用物理失败且未确认停发；必须回收原 Job。", failure, stoppingFailure);
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<QueryObject> IdentityClosure(IEnumerable<uint> roots)
    {
        List<QueryObject> result = [];
        Queue<uint> pending = new(roots);
        HashSet<uint> seen = [];

        while (pending.TryDequeue(out uint address))
        {
            if (address == 0 || !seen.Add(address))
                continue;
            if (seen.Count > 64)
                throw new InvalidDataException("运动对象闭包超过固定上限。");

            QueryObject identity = reader.Identity(address);

            result.Add(identity);
            pending.Enqueue(identity.Outer);
            pending.Enqueue(identity.Class);
        }

        return result;
    }

    /// <summary>
    /// Stops this slot's physics and retires its observation binding. Keeps the shared virtual slot; an
    /// independent PC-only entry retires the state program.
    /// </summary>
    public async Task StopDrivingAsync(CancellationToken token)
    {
        // Even a canceled caller must acquire ownership and stop publication; otherwise active physics could cross the shutdown boundary.
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (!published || poisoned)
                throw new InvalidOperationException("物理停止没有已确认的活动任务；必须回收原 Job。");

            await DisarmAsync(token).ConfigureAwait(false);
            if (reader.U32(command + 8) != reader.U32(command + 12) ||
                reader.U32(command + 0x28) != reader.U32(command + AfdevBioBotMotionHook.ActualPhysicsReturns) ||
                reader.U32(command + AfdevBioBotMotionHook.PhysicsInFlight) != 0)
                throw new InvalidDataException("物理调用返回未知；必须回收原 Job。");

            // Normal Pawn replacement may latch identity-gate failure. Retire only this slot without accessing the old Pawn or restoring the shared virtual slot.
            save("motion-stopped", Read());
            observers.Retire(command);
            await DisarmAsync(token).ConfigureAwait(false);
            published = false;
        }
        catch (Exception failure)
        {
            poisoned = true;
            try
            {
                // Caller cancellation cannot prevent stopping physics publication. Unknown return still requires original-host cleanup.
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
                observers.Retire(command);
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stoppingFailure)
            {
                throw new AggregateException("运动命令失败且未取得停止确认；必须回收原 Job。", failure, stoppingFailure);
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public AfdevBioBotMotionObservation Read() => new(reader.U32(command), reader.U32(command + 4),
        reader.U32(command + 8), reader.U32(command + 12), reader.U32(command + 0x10), reader.U32(command + 0x14),
        reader.U32(command + 0x20), reader.U32(command + 0x24), reader.U32(command + 0x28), reader.U32(command + 0x2C),
        reader.U32(command + AfdevBioBotMotionHook.ActualPhysicsReturns),
        reader.U32(command + AfdevBioBotMotionHook.NaturalPhysicsReturns),
        reader.U32(command + AfdevBioBotMotionHook.DrivenPhysicsEntries),
        reader.U32(command + AfdevBioBotMotionHook.DrivenPhysicsReturns),
        reader.U32(command + AfdevBioBotMotionHook.LastPhysicsCaller),
        reader.U32(command + AfdevBioBotMotionHook.LastNaturalPhysicsCaller),
        reader.U32(command + AfdevBioBotMotionHook.LastDrivenPhysicsCaller),
        reader.U32(command + AfdevBioBotMotionHook.RepeatedPhysicsEntries),
        reader.U32(command + AfdevBioBotMotionHook.PhysicsInFlight),
        reader.U32(command + AfdevBioBotMotionHook.LastPhysicsReturnTick),
        reader.U32(command + AfdevBioBotMotionHook.LastObservedPhysicsTick),
        reader.U32(command + AfdevBioBotMotionHook.NativeCallReceiver),
        reader.U32(command + AfdevBioBotMotionHook.NativeCallDeltaBits),
        reader.U32(command + AfdevBioBotMotionHook.NativeCallStackBefore),
        reader.U32(command + AfdevBioBotMotionHook.NativeCallStackAfter))
    {
        MappedInputSteps = reader.U32(command + AfdevBioBotMovementInput.MappedSteps),
        SourceInputMagnitude = BitConverter.Int32BitsToSingle(unchecked((int)reader.U32(command + AfdevBioBotMovementInput.SourceMagnitude))),
        RequestedInputMagnitude = BitConverter.Int32BitsToSingle(unchecked((int)reader.U32(command + AfdevBioBotMovementInput.RequestedMagnitude)))
    };

    public async Task DisarmAsync(CancellationToken token)
    {
        uint before = reader.U32(command + AfdevBioBotMotionHook.IdleAck);
        var timer = Stopwatch.StartNew();

        process.WriteMemory(command, [0, 0, 0, 0]);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!process.IsAlive)
                throw new IOException("原生实例在运动 IdleAck 前退出。");
            if (reader.U32(command + AfdevBioBotMotionHook.IdleAck) != before &&
                reader.U32(command + AfdevBioBotMotionHook.PhysicsInFlight) == 0 &&
                reader.U32(command + 0x28) == reader.U32(command + AfdevBioBotMotionHook.ActualPhysicsReturns))
            {
                if (timer.Elapsed >= TimeSpan.FromSeconds(5))
                    save("motion-idle-delayed-observation", new DisarmAsyncJson1 { Before = before,
                        IdleAck = reader.U32(command + AfdevBioBotMotionHook.IdleAck), TotalMilliseconds = timer.Elapsed.TotalMilliseconds,
                        Reason = "调度恢复后先核对原生停止收据；已有确定返回时不因迟采样误报未知完成。" });

                return;
            }
            // Long frames or host scheduling may delay continuation. Check received receipts before deciding that timeout actually occurred.
            if (timer.Elapsed >= TimeSpan.FromSeconds(5))
                break;

            await Task.Delay(10, token).ConfigureAwait(false);
        }

        throw new TimeoutException("运动命令没有原生 IdleAck；禁止修改或复用参数页。");
    }
}

internal sealed record AfdevBioBotMotionObservation(uint Mode, uint Status, uint CallsStarted, uint CallsReturned,
    uint CancelStarted, uint CancelReturned, uint LastRequestedTick, uint LastPhysicsTick, uint ActualPhysicsEntries,
    uint NaturalPhysicsEntries, uint ActualPhysicsReturns, uint NaturalPhysicsReturns, uint DrivenPhysicsEntries,
    uint DrivenPhysicsReturns, uint LastPhysicsCaller, uint LastNaturalPhysicsCaller, uint LastDrivenPhysicsCaller,
    uint RepeatedPhysicsEntries, uint PhysicsInFlight, uint LastPhysicsReturnTick, uint LastObservedPhysicsTick,
    uint NativeCallReceiver, uint NativeCallDeltaBits, uint NativeCallStackBefore, uint NativeCallStackAfter)
{
    public uint MappedInputSteps { get; init; }
    public float SourceInputMagnitude { get; init; }
    public float RequestedInputMagnitude { get; init; }
}
