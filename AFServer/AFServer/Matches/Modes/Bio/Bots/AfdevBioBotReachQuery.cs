using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Serializes native arrival queries. Command-page reuse requires confirmed return followed by IdleAck.
/// </summary>
internal sealed partial class AfdevBioBotReachQuery(AfdevOwnedProcess process, AfdevNativeEvidence world,
    AfdevBioBotPreparation participant, Guid botId, uint onlinePage, uint command,
    Action<string, object> save, Action checkBinding)
{
    private const uint ReachedDestinationExec = 0x00953610;
    private const uint ReachedPointExec = 0x00953680;
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool failed;
    private uint ordinal;
    private bool lifecycleRetired;

    public async Task<AfdevBioBotReachResult> ExecuteAsync(uint goal, CancellationToken token, uint expectedPawn = 0,
        AfdevBioBotPoint? point = null)
    {
        if (point is { } position && (!position.IsValid || goal != 0))
            throw new ArgumentException("坐标到达查询不接受 Actor 地址或非有限坐标。");

        await gate.WaitAsync(token).ConfigureAwait(false);

        uint next = ordinal;
        bool requested = false;
        ReachTarget? target = null;

        try
        {
            if (failed || ordinal == uint.MaxValue)
                throw new InvalidOperationException("到达查询已有未知完成或序号已耗尽，必须回收原 Job。");

            checkBinding();
            await DisarmAsync(token).ConfigureAwait(false);

            uint currentPawn = reader.U32(reader.FieldAddress(participant.Controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4));

            if (expectedPawn == 0)
                expectedPawn = currentPawn;
            if (currentPawn == 0 || currentPawn != expectedPawn)
                throw new AfdevBioBotActionInterruptedException("到达查询前 Pawn 已更换。");

            next = ordinal + 1;
            target = point.HasValue ? null : CaptureTarget(goal);

            if (TargetChanged(target))
                throw new AfdevBioBotActionInterruptedException("到达查询前目标已死亡或换代。");

            byte[] descriptor = Describe(goal, next, target, point, out QueryObject pawn);

            if (pawn.Address != expectedPawn)
                throw new AfdevBioBotActionInterruptedException("到达查询描述不属于当前路线的 Pawn。");

            // IdleAck and completed ordinals reside in a nonrewritable page tail to avoid publication/readback races.
            process.WriteMemory(command, descriptor);

            if (!process.ReadMemory(command, descriptor.Length).AsSpan().SequenceEqual(descriptor))
                throw new IOException("到达查询描述页发布前回读不一致。");

            token.ThrowIfCancellationRequested();
            requested = true;
            lifecycleRetired = false;
            process.WriteMemory(command, [1, 0, 0, 0]);

            var elapsed = Stopwatch.StartNew();

            while (reader.U32(command + 8) == 0)
            {
                token.ThrowIfCancellationRequested();
                checkBinding();

                if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("原生到达查询完成未知，禁止重发。");

                await Task.Delay(10, token).ConfigureAwait(false);
            }

            byte[] completed = process.ReadMemory(command, 0x40);

            uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(completed.AsSpan(offset, 4));

            save("reach-query-receipt", new ExecuteAsyncJson1
            {
                next = next, goal = goal, point = point, pawn = pawn, HeaderHex = Convert.ToHexString(completed),
                CompletedOrdinal = reader.U32(command + AfdevBioBotReachHook.CompletedOrdinal)
            });

            if (Word(0) != 1 || Word(4) != 1 || Word(8) != 1 || Word(12) != 1 || Word(16) > 1 ||
                Word((int)AfdevBioBotReachHook.StartedOrdinal) != next ||
                Word((int)AfdevBioBotReachHook.ReturnedOrdinal) != next || Word(0x20) != Word(0x24) ||
                reader.U32(command + AfdevBioBotReachHook.CompletedOrdinal) != next)
                throw new InvalidDataException("原生到达查询未返回有效结果或失去精确身份，必须回收原 Job。");

            await DisarmAsync(token).ConfigureAwait(false);
            checkBinding();
            ordinal = next;

            if (TargetChanged(target))
                throw new AfdevBioBotActionInterruptedException("到达查询返回后目标已死亡或换代。");

            var result = new AfdevBioBotReachResult(participant.InstanceId, participant.AttemptId,
                participant.AllocationId, participant.WorldGeneration, botId, ordinal, participant.Controller,
                participant.Pri, pawn.Address, goal, Word(16) == 1, DateTimeOffset.UtcNow) { Point = point };

            save("reach-query-result", result);
            return result;
        }
        catch (Exception error)
        {
            try
            {
                // Stopping publication cannot use an already canceled token. Calls with unconfirmed return are reclaimed with the original Job.
                await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stopping)
            {
                failed = true;
                throw new AggregateException("到达查询失败且未取得退出确认，必须回收原 Job。", error, stopping);
            }

            uint started = reader.U32(command + AfdevBioBotReachHook.StartedOrdinal);
            uint returned = reader.U32(command + AfdevBioBotReachHook.ReturnedOrdinal);
            bool settled = !requested || started == 0 && returned == 0 && reader.U32(command + 4) == 0 ||
                started == next && returned == next && reader.U32(command + 8) == 1 &&
                reader.U32(command + 0x20) == reader.U32(command + 0x24);
            uint currentPawn = reader.U32(reader.FieldAddress(participant.Controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4));
            bool pawnChanged = error is AfdevBioBotActionInterruptedException || expectedPawn != 0 &&
                (currentPawn != expectedPawn || unchecked((int)reader.U32(reader.FieldAddress(expectedPawn,
                    "Health", "Engine.Pawn.Health", "IntProperty", 4))) <= 0);
            bool targetChanged = target != null && TargetChanged(target);

            if (settled && (pawnChanged || targetChanged || error is OperationCanceledException && token.IsCancellationRequested))
            {
                // Discard old-generation results only before calling or after paired return. Failed ordinals are not valid arrival and never replay old-Pawn queries.
                if (requested && reader.U32(command + AfdevBioBotReachHook.CompletedOrdinal) == next)
                    ordinal = next;

                lifecycleRetired = true;
                save("reach-query-interrupted", new ExecuteAsyncJson2 { ordinal = ordinal, goal = goal, expectedPawn = expectedPawn, started = started, returned = returned, targetChanged = targetChanged, Message = error.Message });

                if (error is OperationCanceledException)
                    throw;

                throw new AfdevBioBotActionInterruptedException("到达查询已安全退出，自身或目标 Pawn 失效后重新规划。");
            }

            failed = true;
            save("reach-query-failed", new ExecuteAsyncJson3 { ordinal = ordinal, goal = goal, Message = error.Message, Detail = error.ToString() });
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (!process.IsAlive)
                return;

            await DisarmAsync(token).ConfigureAwait(false);

            if (failed || !lifecycleRetired && reader.U32(command + 4) != 0 &&
                (reader.U32(command + 8) != 1 || reader.U32(command + 12) != 1))
                throw new IOException("到达查询存在失败或未知完成，必须回收原 Job。");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DisarmAsync(CancellationToken token)
    {
        if (!process.IsAlive)
            return;

        uint before = reader.U32(command + AfdevBioBotReachHook.IdleAck);
        var elapsed = Stopwatch.StartNew();

        process.WriteMemory(command, [0, 0, 0, 0]);

        while (process.IsAlive && reader.U32(command + AfdevBioBotReachHook.IdleAck) == before)
        {
            token.ThrowIfCancellationRequested();

            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("到达查询未取得游戏线程退出确认。");

            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    private ReachTarget CaptureTarget(uint goal)
    {
        QueryObject identity = reader.Identity(goal);

        if (!reader.IsA(goal, "Engine.Pawn"))
            return new ReachTarget(identity, null, 0, 0, 0);

        uint controllerField = reader.FieldAddress(goal, "Controller", "Engine.Pawn.Controller", "ObjectProperty", 4);
        uint controller = reader.U32(controllerField);

        if (controller < 0x10000)
            throw new AfdevBioBotActionInterruptedException("到达查询目标 Pawn 已失去控制器。");

        return new ReachTarget(identity, reader.Identity(controller), controllerField,
            reader.FieldAddress(controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4),
            reader.FieldAddress(goal, "Health", "Engine.Pawn.Health", "IntProperty", 4));
    }

    private bool TargetChanged(ReachTarget? target)
    {
        if (target == null)
            return false;

        try
        {
            return reader.Identity(target.Identity.Address) != target.Identity ||
                (reader.U32(target.Identity.Address + 0x0C) & 0x20000000) != 0 ||
                (reader.U32(target.Identity.Address + 0xB0) & 8) != 0 ||
                target.Controller is { } controller && (reader.Identity(controller.Address) != controller ||
                    reader.U32(target.ControllerField) != controller.Address || reader.U32(target.PawnField) != target.Identity.Address ||
                    unchecked((int)reader.U32(target.HealthField)) <= 0);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            // Use this check only after obtaining the exact snapshot. Ordinals, stack checks, and IdleAck independently confirm call exit.
            return true;
        }
    }

    private sealed record ReachTarget(QueryObject Identity, QueryObject? Controller, uint ControllerField,
        uint PawnField, uint HealthField);

    private byte[] Describe(uint goal, uint next, ReachTarget? target, AfdevBioBotPoint? point, out QueryObject pawnIdentity)
    {
        checkBinding();

        uint pc = participant.Controller;
        uint pri = participant.Pri;
        uint pawn = reader.U32(reader.FieldAddress(pc, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4));

        pawnIdentity = reader.Identity(pawn);

        if (reader.Path(reader.Identity(pc).Class) != "TGBioGame.TGBioPlayerController" ||
            reader.Path(reader.Identity(pri).Class) != "TGBioGame.TGBioPlayerReplicationInfo" ||
            !reader.IsA(pawn, "TGBioGame.TGBioPawn") ||
            (!point.HasValue && !reader.IsA(goal, "Engine.NavigationPoint") && !reader.IsA(goal, "Engine.Pawn")))
            throw new InvalidDataException("到达查询只接受本次 BIO 参赛者及导航点或 Pawn 目标。");

        var roots = new List<uint> { pc, pri, pawn, goal, world.GameInfo, world.WorldInfo };
        var proofs = new Dictionary<(uint Address, uint Mask), uint>();
        byte[] payload = new byte[AfdevBioBotReachHook.PayloadSize];

        void Put(uint offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan((int)offset, 4), value);
        void Prove(uint address, uint value, uint mask = uint.MaxValue)
        {
            if ((reader.U32(address) & mask) != value)
                throw new InvalidDataException($"到达查询描述不符：0x{address:X8}。");

            proofs[(address, mask)] = value;
        }

        void ProveBytes(uint address, byte[] bytes)
        {
            for (int offset = 0; offset < bytes.Length; offset += 4)
            {
                int length = Math.Min(4, bytes.Length - offset);
                uint value = 0;

                for (int index = 0; index < length; index++)
                    value |= (uint)bytes[offset + index] << (index * 8);

                Prove(address + (uint)offset, value, length == 4 ? uint.MaxValue : (1u << (length * 8)) - 1);
            }
        }

        uint Field(uint receiver, string path, string kind = "ObjectProperty", uint size = 4)
        {
            string name = path[(path.LastIndexOf('.') + 1)..];
            uint property = reader.FindField(reader.Identity(receiver).Class, name, path);
            uint address = reader.FieldAddress(receiver, name, path, kind, size);

            roots.Add(property);
            Prove(property + 0x40, 1);
            Prove(property + 0x44, size);
            Prove(property + 0x60, address - receiver);
            return address;
        }

        string functionName = point.HasValue ? "ReachedPoint" : "ReachedDestination";
        string functionPath = "Engine.Pawn." + functionName;
        uint function = reader.FindField(pawnIdentity.Class, functionName, functionPath);
        QueryObject functionIdentity = reader.Identity(function);
        uint script = reader.U32(function + 0x54);
        int capacity = unchecked((int)reader.U32(function + 0x5C));
        uint goalParameter = reader.U32(function + 0x4C);
        uint anchorParameter = point.HasValue ? reader.U32(goalParameter + 0x3C) : 0;
        uint returnedParameter = reader.U32((point.HasValue ? anchorParameter : goalParameter) + 0x3C);
        uint nativeExec = point.HasValue ? ReachedPointExec : ReachedDestinationExec;
        uint parameterSize = point.HasValue ? 20u : 8u;
        uint returnOffset = point.HasValue ? 16u : 4u;
        uint scriptCount = point.HasValue ? 20u : 11u;

        if (reader.Path(functionIdentity.Class) != "Core.Function" || script < 0x10000 ||
            capacity < scriptCount || capacity > scriptCount + 4096)
            throw new InvalidDataException("原生到达函数或脚本边界无效。");

        roots.AddRange([function, goalParameter, anchorParameter, returnedParameter]);

        foreach ((uint offset, uint value, uint mask) in new[]
        {
            (0x50u, parameterSize, uint.MaxValue), (0x54u, script, uint.MaxValue), (0x58u, scriptCount, uint.MaxValue),
            (0x5Cu, (uint)capacity, uint.MaxValue), (0xACu, 0x20400u, uint.MaxValue), (0xB0u, 0u, 0xFFFFu),
            (0xBEu, parameterSize, 0xFFFFu), (0xC0u, returnOffset, 0xFFFFu), (0xC8u, nativeExec, uint.MaxValue),
            (0x4Cu, goalParameter, uint.MaxValue)
        })
            Prove(function + offset, value, mask);

        (uint Parameter, string Name, string Kind, uint Offset, uint Size, uint Flags, uint Following)[] parameters = point.HasValue
            ? [(goalParameter, "Point", "StructProperty", 0, 12, 0x80, anchorParameter),
               (anchorParameter, "NewAnchor", "ObjectProperty", 12, 4, 0x80, returnedParameter),
               (returnedParameter, "ReturnValue", "BoolProperty", 16, 4, 0x580, 0)]
            : [(goalParameter, "Goal", "ObjectProperty", 0, 4, 0x80, returnedParameter),
               (returnedParameter, "ReturnValue", "BoolProperty", 4, 4, 0x580, 0)];

        foreach ((uint parameter, string name, string kind, uint offset, uint size, uint flags, uint following) in parameters)
        {
            QueryObject identity = reader.Identity(parameter);

            if (identity.Outer != function || reader.Path(parameter) != functionPath + "." + name ||
                reader.Path(identity.Class) != "Core." + kind)
                throw new InvalidDataException("ReachedDestination 参数类型或归属发生变化。");

            Prove(parameter + 0x3C, following);
            Prove(parameter + 0x40, 1);
            Prove(parameter + 0x44, size);
            Prove(parameter + 0x48, flags);
            Prove(parameter + 0x4C, 0);
            Prove(parameter + 0x60, offset);
        }

        uint actorParameter = point.HasValue ? anchorParameter : goalParameter;
        uint actorType = reader.U32(actorParameter + 0x84);

        if (reader.Path(actorType) != "Engine.Actor")
            throw new InvalidDataException("ReachedDestination.Goal 不是原生 Actor 参数。");

        roots.Add(actorType);
        Prove(actorParameter + 0x84, actorType);
        Prove(returnedParameter + 0x84, 1);

        if (point.HasValue)
        {
            uint vectorType = reader.U32(goalParameter + 0x84);

            if (reader.Path(vectorType) != "Core.Object.Vector" || reader.Path(reader.Identity(vectorType).Class) != "Core.ScriptStruct")
                throw new InvalidDataException("ReachedPoint.Point 不是原生 Vector。");

            roots.Add(vectorType);
            Prove(goalParameter + 0x84, vectorType);
            Prove(vectorType + 0x50, 12);
        }

        // VM references occupy eight bytes: current UObject in the low DWORD, zero in the high DWORD.
        byte[] expectedScript = new byte[scriptCount];

        expectedScript[0] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(expectedScript.AsSpan(1, 4), goalParameter);

        if (point.HasValue)
        {
            expectedScript[9] = 0x29;
            BinaryPrimitives.WriteUInt32LittleEndian(expectedScript.AsSpan(10, 4), anchorParameter);
        }

        expectedScript[^2] = 0x0B;
        expectedScript[^1] = 0x53;
        ProveBytes(script, expectedScript);

        uint vtable = reader.U32(pawn);
        uint goalVtable = point.HasValue ? 0 : reader.U32(goal);
        uint pawnDestinationOffset = reader.U32(vtable + 0x3E4);

        if (pawnDestinationOffset is < 0x00400000 or >= 0x03000000)
            throw new InvalidDataException("到达查询下游虚槽不属于当前原生映像。");

        Prove(pawn, vtable);
        Prove(vtable + 0x3E4, pawnDestinationOffset);

        if (!point.HasValue)
        {
            uint goalCustomReach = reader.U32(goalVtable + 0x2F8);

            if (goalCustomReach is < 0x00400000 or >= 0x03000000)
                throw new InvalidDataException("目标的到达虚槽不属于当前映像。");

            Prove(goal, goalVtable);
            Prove(goalVtable + 0x2F8, goalCustomReach);
            // NavigationPoint destination offsets differ from ordinary Pawn offsets.
            Prove(goalVtable + 0x164, reader.IsA(goal, "Engine.NavigationPoint") ? 0x00B66EC0u : 0x008EBA10u);
        }
        else
        {
            Prove(vtable + 0x40C, 0x00B6B410);
        }

        foreach ((uint offset, uint expectedExec) in new[]
        {
            (0x120u, QueryHook.ActorProcessEvent), (0x408u, 0x00B642B0u),
            (0x404u, 0x013AA2B0u), (0x490u, 0x013BFF30u)
        })
            Prove(vtable + offset, expectedExec);

        VerifyNativeBodies();

        Put(0x10, AfdevBioBotReachHook.ResultSentinel);
        Put(0x40, process.MainThreadId);
        Put(0x44, reader.U32(0x02063820));
        Put(0x48, world.World);
        Put(0x4C, world.WorldInfo);
        Put(0x50, world.GameInfo);
        Put(0x54, pc);
        Put(0x58, pri);
        Put(0x5C, pawn);
        Put(0x60, goal);
        Put(0x64, function);
        Put(0x68, nativeExec);
        Put(AfdevBioBotReachHook.PointMode, point.HasValue ? 1u : 0u);
        Put(0x80, next);
        Put(0x84, Field(pri, "Engine.PlayerReplicationInfo.bBot", "BoolProperty"));

        uint botProperty = reader.FindField(reader.Identity(pri).Class, "bBot", "Engine.PlayerReplicationInfo.bBot");
        uint botMask = reader.U32(botProperty + 0x84);

        if (botMask == 0 || (botMask & (botMask - 1)) != 0)
            throw new InvalidDataException("到达查询的内部 Bot 标志布局无效。");

        Prove(botProperty + 0x84, botMask);
        Put(0x88, botMask);
        Put(0x8C, Field(pc, "Engine.PlayerController.Player"));
        Put(0x90, Field(pc, "Engine.Controller.PlayerReplicationInfo"));
        Put(0x94, Field(pri, "Engine.Actor.Owner"));
        Put(0x98, Field(pc, "Engine.Controller.Pawn"));
        Put(0x9C, Field(pawn, "Engine.Pawn.Controller"));
        Put(0xA0, Field(pawn, "Engine.Actor.WorldInfo"));
        Put(0xA4, Field(pc, "Engine.Actor.WorldInfo"));
        Put(0xA8, Field(pri, "Engine.Actor.WorldInfo"));
        if (!point.HasValue)
            Put(0xAC, Field(goal, "Engine.Actor.WorldInfo"));

        Put(0xB0, Field(pawn, "Engine.Pawn.Health", "IntProperty"));
        Prove(Field(pawn, "Engine.Pawn.PlayerReplicationInfo"), pri);

        if (target?.Controller is { } targetController)
        {
            roots.Add(targetController.Address);
            Put(0xB4, targetController.Address);
            Put(0xB8, Field(goal, "Engine.Pawn.Health", "IntProperty"));
            Put(0xBC, Field(goal, "Engine.Pawn.Controller"));
            Put(0xC0, Field(targetController.Address, "Engine.Controller.Pawn"));

            if (TargetChanged(target))
                throw new AfdevBioBotActionInterruptedException("到达查询描述期间目标 Pawn 已失效。");
        }

        uint uinAddress = Field(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12);
        uint uinData = reader.U32(uinAddress);
        int uinCount = unchecked((int)reader.U32(uinAddress + 4));
        int uinCapacity = unchecked((int)reader.U32(uinAddress + 8));
        byte[] expectedUin = Encoding.Unicode.GetBytes(participant.Uin + '\0');

        if (participant.Uin.Length != 16 || uinData < 0x10000 || uinCount != 17 ||
            uinCapacity < uinCount || uinCapacity > 1024 || botId == Guid.Empty || onlinePage < 0x10000)
            throw new InvalidDataException("到达查询缺少完整内部身份或合法 FString 边界。");

        Put(0xD0, uinAddress);
        Put(0xD4, (uint)uinCount);
        Put(AfdevBioBotReachHook.ExpectedUinCapacity, (uint)uinCapacity);
        expectedUin.CopyTo(payload, (int)AfdevBioBotReachHook.Uin);
        Prove(uinAddress, uinData);

        // The active isolation page carries managed lifetime identity into the same game thread, beyond host-side address checks.
        Prove(onlinePage, 1);
        Prove(onlinePage + 4, 0);
        Prove(onlinePage + 0x40, world.World);
        Prove(onlinePage + 0x44, world.WorldInfo);
        Prove(onlinePage + 0x48, world.GameInfo);
        Prove(onlinePage + 0x4C, pc);
        Prove(onlinePage + 0x50, pri);
        ProveBytes(onlinePage + 0x100, participant.InstanceId.ToByteArray());
        ProveBytes(onlinePage + 0x110, participant.AttemptId.ToByteArray());
        ProveBytes(onlinePage + 0x120, participant.AllocationId.ToByteArray());
        ProveBytes(onlinePage + 0x130, botId.ToByteArray());
        Prove(onlinePage + 0x140, unchecked((uint)participant.WorldGeneration));
        Prove(onlinePage + 0x144, unchecked((uint)(participant.WorldGeneration >> 32)));
        ProveBytes(onlinePage + 0x200, expectedUin);
        if (point is { } destination)
        {
            float[] coordinates = destination.ToArray();

            for (int axis = 0; axis < coordinates.Length; axis++)
            {
                uint bits = BitConverter.SingleToUInt32Bits(coordinates[axis]);

                Put(AfdevBioBotReachHook.Parameters + (uint)axis * 4, bits);
                Put(AfdevBioBotReachHook.Point + (uint)axis * 4, bits);
            }

            // NewAnchor=None: ReachedPoint tests arrival without setting the native navigation anchor.
            Put(AfdevBioBotReachHook.Parameters + 12, 0);
        }
        else
        {
            Put(AfdevBioBotReachHook.Parameters, goal);
        }

        Put(AfdevBioBotReachHook.Parameters + returnOffset, AfdevBioBotReachHook.ResultSentinel);

        IReadOnlyList<QueryObject> identities = IdentityClosure(roots);

        if (proofs.Count > AfdevBioBotReachHook.ProofCapacity ||
            reader.U32(command + AfdevBioBotReachHook.CompletedOrdinal) != ordinal)
            throw new InvalidDataException("到达查询的证明容量或已完成序号不符。");

        Put(0x78, (uint)identities.Count);
        Put(0x7C, (uint)proofs.Count);

        uint cursor = AfdevBioBotReachHook.IdentityStart;

        foreach (QueryObject identity in identities)
            foreach (uint value in new[] { identity.Address, identity.Index, identity.Outer, identity.NameIndex, identity.NameNumber, identity.Class })
            {
                Put(cursor, value);
                cursor += 4;
            }

        cursor = AfdevBioBotReachHook.ProofStart;

        foreach (((uint address, uint mask), uint value) in proofs)
        {
            Put(cursor, address);
            Put(cursor + 4, value);
            Put(cursor + 8, mask);
            cursor += 12;
        }

        save("reach-query-prepared-" + next, new DescribeJson1
        {
            next = next, pawn = pawnIdentity, Goal = target?.Identity, Point = point, function = function, vtable = vtable, goalVtable = goalVtable,
            Identities = identities, ProofCount = proofs.Count, NativeExec = nativeExec,
            RuntimeScriptHex = Convert.ToHexString(expectedScript), RuntimeScriptSha256 = TickHook.Hash(expectedScript),
            DescriptorHex = Convert.ToHexString(payload), NativeResultSentinel = AfdevBioBotReachHook.ResultSentinel,
            Parameters = point.HasValue ? "Vector +0; NewAnchor=None +12; bool ReturnValue +16; size 20"
                : "Goal +0; bool ReturnValue +4; size 8", NativeImageSha256 = world.AfdevSha256
        });
        return payload;
    }

    private IReadOnlyList<QueryObject> IdentityClosure(IEnumerable<uint> roots)
    {
        var pending = new Queue<uint>(roots);
        var seen = new HashSet<uint>();
        var result = new List<QueryObject>();

        while (pending.TryDequeue(out uint address))
        {
            if (address == 0 || !seen.Add(address))
                continue;
            if (seen.Count > AfdevBioBotReachHook.IdentityCapacity)
                throw new InvalidDataException("到达查询对象闭包超过固定容量。");

            QueryObject identity = reader.Identity(address);

            result.Add(identity);
            pending.Enqueue(identity.Outer);
            pending.Enqueue(identity.Class);
        }

        return result;
    }

    private void VerifyNativeBodies()
    {
        foreach ((uint start, int length, string hash) in new[]
        {
            (ReachedDestinationExec, 0x65, "69a916ed4afd3ad3a9e7e2d1947be53e03e90ff6fbed87e6db7c8fa3b859106d"),
            (ReachedPointExec, 158, "462adaba3c13e9e3a6ea66867e819f053cd62dc5e0ef5a66d4be4f1b696147d7"),
            (0x00B6B410u, 77, "897652f0eaacfe4abd60104e7bf8258a8dc9398c8659edc3602ebe3a16e3f49e"),
            (0x008EBA10u, 178, "bb69b700807f15da2cedbd739ee2c592bacb8e6517c986c0eb05e5df2862a8fe"),
            (0x00B66EC0u, 775, "5d933507c925520adf284a64a9c0ff4731c12bda0740f53285aa5c06d2d32da4"),
            (0x00B671D0u, 1442, "2355e7923eba5797bd72333b1c9ded1d5f2af0576b7dbe0c9db49c33fc913a91"),
            (0x00B63890u, 24, "764c9e8c0f95c1164ac0f4c083b4216ae00f1cb7bb7f5cf17c47559115dffc95"),
            (0x00B642B0u, 0x4A, "ccd684c8004a5564862387f2917a25b56f9eda4a08d88210ae55bb363ec8b300"),
            (0x013AA2B0u, 0xEA, "a266a8cf15ee665ae488b1b613f8dd9ad1b8efca5db62e67f9585b8218406590"),
            (0x013BFF30u, 0x57C, "d4d0504af882186a33b6fafe7966e84846c3cfe9ab269b33b565ba9f523a3814")
        })
            if (TickHook.Hash(process.ReadMemory(start, length)) != hash)
                throw new InvalidDataException("原生到达入口或判定主体的完整哈希不同。");
    }
}

/// <summary>
/// Valid native ReachedDestination return. Missing execution, rejection, or unknown completion throws
/// rather than fabricating false.
/// </summary>
public sealed record AfdevBioBotReachResult(Guid InstanceId, Guid AttemptId, Guid AllocationId, long WorldGeneration,
    Guid BotId, uint Ordinal, uint Controller, uint Pri, uint Pawn, uint Goal, bool Reached, DateTimeOffset ObservedAtUtc)
{
    public AfdevBioBotPoint? Point { get; init; }
}
