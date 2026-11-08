using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Freezes an independent PC-only retirement descriptor per navigation action. Single-slot reuse requires
/// game-thread IdleAck.
/// </summary>
internal sealed partial class AfdevBioBotNavigationStop(AfdevOwnedProcess process, AfdevNativeEvidence world,
    AfdevBioBotPreparation participant, Guid botId, uint onlinePage, uint command, Action<string, object> save)
{
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private uint ordinal;
    private bool prepared;
    private bool failed;

    public async Task PrepareAsync(Guid taskId, uint frame, uint code, AfdevNavigationProgram descriptor, CancellationToken token)
    {
        byte[] program = descriptor.Program;

        if (failed || ordinal == uint.MaxValue || program.Length is < 2 or > 128 ||
            descriptor.StopOffset != program.Length - 1 || program[^1] != 8 || descriptor.LatentIndex is not (501 or 503))
            throw new InvalidOperationException("导航退休描述已失效或不是固定自有程序。");

        await DisarmAsync(token).ConfigureAwait(false);

        uint pc = participant.Controller;
        uint pri = participant.Pri;
        uint node = reader.U32(frame + 0x10);
        uint state = reader.U32(frame + 0x28);
        ResourceFunction stop = new AfdevBioBotMotionFunctions(process).DescribeStopLatentExecution(pc);
        var roots = new List<uint> { pc, pri, world.World, world.WorldInfo, world.GameInfo, node, state, stop.Function.Address };
        var proofs = new Dictionary<(uint Address, uint Mask), uint>();
        byte[] payload = new byte[AfdevBioBotNavigationStopHook.PayloadSize];

        void Put(uint offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan((int)offset, 4), value);
        void Prove(uint address, uint value, uint mask = uint.MaxValue)
        {
            if ((reader.U32(address) & mask) != value)
                throw new InvalidDataException($"导航退休描述不符：0x{address:X8}。");

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

        Prove(Field(pc, "Engine.PlayerController.Player"), 0);
        Prove(Field(pc, "Engine.Controller.PlayerReplicationInfo"), pri);
        Prove(Field(pri, "Engine.Actor.Owner"), pc);
        Prove(Field(pc, "Engine.Actor.WorldInfo"), world.WorldInfo);
        Prove(Field(pri, "Engine.Actor.WorldInfo"), world.WorldInfo);
        Prove(Field(pc, "Engine.Actor.Role", "ByteProperty", 1), 3, 0xFF);
        Prove(pc + 0x0C, 0, 0x20000000);
        Prove(pc + 0xB0, 0, 8);
        Prove(pri + 0xB0, 0, 8);

        uint botField = Field(pri, "Engine.PlayerReplicationInfo.bBot", "BoolProperty");
        uint botProperty = reader.FindField(reader.Identity(pri).Class, "bBot", "Engine.PlayerReplicationInfo.bBot");
        uint botMask = reader.U32(botProperty + 0x84);

        if (botMask == 0 || (botMask & (botMask - 1)) != 0)
            throw new InvalidDataException("导航退休的 Bot 标志不是单一原生位。");

        Prove(botProperty + 0x84, botMask);
        Prove(botField, botMask, botMask);

        uint uinAddress = Field(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12);
        uint uinData = reader.U32(uinAddress);
        uint capacity = reader.U32(uinAddress + 8);
        byte[] uin = Encoding.Unicode.GetBytes(participant.Uin + '\0');

        if (uin.Length != 34 || uinData < 0x10000 || capacity is < 17 or > 1024 || botId == Guid.Empty)
            throw new InvalidDataException("导航退休缺少完整内部身份。");

        Prove(uinAddress, uinData);
        Prove(uinAddress + 4, 17);
        Prove(uinAddress + 8, capacity);
        ProveBytes(uinData, uin);
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
        ProveBytes(onlinePage + 0x140, BitConverter.GetBytes(participant.WorldGeneration));
        ProveBytes(onlinePage + 0x148, BitConverter.GetBytes(participant.ProcessCreationTimeUtc.UtcTicks));
        ProveBytes(onlinePage + 0x200, uin);

        uint function = stop.Function.Address;
        uint vtable = reader.U32(pc);

        Prove(pc, vtable);
        Prove(vtable + 0x120, QueryHook.ActorProcessEvent);
        Prove(vtable + 0x128, 0xC102D0);
        Prove(function + 0xAC, stop.Flags);
        Prove(function + 0xB0, 0, 0xFFFF);
        Prove(function + 0xBE, 0, 0xFFFF);
        Prove(function + 0xC0, 0xFFFF, 0xFFFF);
        Prove(function + 0xC8, stop.ExecPointer);
        Prove(function + 0x4C, 0);
        Prove(function + 0x50, 0);
        Prove(function + 0x54, stop.ScriptPointer);
        Prove(function + 0x58, stop.ScriptCount);
        Prove(function + 0x5C, stop.ScriptCapacity);
        ProveBytes(stop.ScriptPointer, [0x0B, 0x53]);
        ProveBytes(code, program);

        // FStateFrame +0x32 is a BYTE; +0x33 is uninitialized padding. Native constructor 0x004E96E0 and
        // copy constructor 0x00C0D020 each access only one byte. Adjacent padding must not be mistaken for debug state.
        byte[] debug = process.ReadMemory(frame + 0x32, 2);

        // The native gate reads all frame fields after confirming PC.StateFrame. No old Pawn is checked here either.
        save("navigation-stop-frame", new PrepareAsyncJson1 { pc = pc, frame = frame, CurrentFrame = reader.U32(pc + 0x18),
            Object = reader.U32(frame + 0x14), Debug = debug[0], Padding = debug[1],
            StackData = reader.U32(frame + 0x34), StackCount = reader.U32(frame + 0x38),
            StackCapacity = reader.U32(frame + 0x3C), Debugger = reader.U32(0x01FD6A08),
            FrameHex = Convert.ToHexString(process.ReadMemory(frame, 0x40)) });

        if (reader.U32(pc + 0x18) != frame || reader.U32(frame + 0x14) != pc ||
            debug[0] != 0 || reader.U32(frame + 0x34) != 0 ||
            reader.U32(frame + 0x38) != 0 || reader.U32(frame + 0x3C) != 0 || reader.U32(0x01FD6A08) != 0)
            throw new AfdevBioBotActionInterruptedException("等待无 debugger、空状态栈且归属稳定的控制器帧；尚未发布导航。");

        var identities = new List<QueryObject>();
        var pending = new Queue<uint>(roots);
        var seen = new HashSet<uint>();

        while (pending.TryDequeue(out uint address))
        {
            if (address == 0 || !seen.Add(address))
                continue;
            if (seen.Count > AfdevBioBotNavigationStopHook.IdentityCapacity)
                throw new InvalidDataException("导航退休身份闭包超出固定槽位。");

            QueryObject identity = reader.Identity(address);

            identities.Add(identity);
            pending.Enqueue(identity.Outer);
            pending.Enqueue(identity.Class);
        }

        if (proofs.Count > AfdevBioBotNavigationStopHook.ProofCapacity)
            throw new InvalidDataException("导航退休元数据超出固定槽位。");

        Put(0x18, ++ordinal);
        Put(0x40, process.MainThreadId); Put(0x44, reader.U32(0x02063820));
        Put(0x48, world.World); Put(0x4C, world.WorldInfo); Put(0x50, world.GameInfo);
        Put(0x54, pc); Put(0x58, frame); Put(0x5C, node); Put(0x60, state);
        Put(0x64, code); Put(0x68, code + descriptor.StopOffset); Put(0x6C, function);
        Put(0x74, (uint)identities.Count); Put(0x78, (uint)proofs.Count);
        Put(AfdevBioBotNavigationStopHook.ExpectedLatent, descriptor.LatentIndex);

        uint cursor = AfdevBioBotNavigationStopHook.IdentityStart;

        foreach (QueryObject identity in identities)
        {
            foreach (uint value in new[] { identity.Address, identity.Index, identity.Outer, identity.NameIndex, identity.NameNumber, identity.Class })
            {
                Put(cursor, value);
                cursor += 4;
            }
        }

        cursor = AfdevBioBotNavigationStopHook.ProofStart;

        foreach (var proof in proofs)
        {
            Put(cursor, proof.Key.Address); Put(cursor + 4, proof.Value); Put(cursor + 8, proof.Key.Mask);
            cursor += 12;
        }

        process.WriteMemory(command, payload);

        if (!process.ReadMemory(command, payload.Length).AsSpan().SequenceEqual(payload))
            throw new IOException("导航退休描述回读失败。");

        prepared = true;
        save("navigation-stop-prepared", new PrepareAsyncJson2 { taskId = taskId, ordinal = ordinal, InstanceId = participant.InstanceId, AttemptId = participant.AttemptId,
            AllocationId = participant.AllocationId, WorldGeneration = participant.WorldGeneration, botId = botId, pc = pc, pri = pri, frame = frame, node = node, state = state, code = code,
            Stop = code + descriptor.StopOffset, LatentIndex = descriptor.LatentIndex,
            Identities = identities, ProofCount = proofs.Count, PawnIndependent = true });
    }

    public async Task<AfdevBioBotProgramRetirement> RetireAsync(CancellationToken token)
    {
        if (!prepared || failed)
            throw new InvalidOperationException("导航退休没有有效的已冻结描述。");

        try
        {
            process.WriteMemory(command, [1, 0, 0, 0]);

            var elapsed = Stopwatch.StartNew();

            while (reader.U32(command + 4) == 0)
            {
                token.ThrowIfCancellationRequested();

                if (!process.IsAlive || elapsed.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("导航退休未确认原生 Code/latent 清除，必须回收原 Job。");

                await Task.Delay(10, token).ConfigureAwait(false);
            }

            await DisarmAsync(token).ConfigureAwait(false);

            var result = new AfdevBioBotProgramRetirement(ordinal, reader.U32(command + 4),
                reader.U32(command + 8), reader.U32(command + 12), reader.U32(command + 0x10),
                reader.U32(command + 0x14), reader.U32(command + 0x20),
                reader.U32(command + AfdevBioBotNavigationStopHook.NativeStateChanged) == 1,
                reader.U32(command + AfdevBioBotNavigationStopHook.ObservedNode),
                reader.U32(command + AfdevBioBotNavigationStopHook.ObservedState),
                reader.U32(command + AfdevBioBotNavigationStopHook.ObservedLatent));
            bool nativeStateDetached = result.NativeStateChanged &&
                (result.Node != reader.U32(command + 0x5C) || result.State != reader.U32(command + 0x60)) &&
                (result.Code < reader.U32(command + 0x64) || result.Code > reader.U32(command + 0x68));
            bool ownedStateCleared = !result.NativeStateChanged && result.Code == 0 && result.LatentAction == 0;

            save("navigation-stop-result", result);

            if (result.Status != 1 || result.CallsStarted != result.CallsReturned || result.CallsStarted > 1 ||
                (!nativeStateDetached && !ownedStateCleared))
                throw new InvalidDataException("导航退休的控制器、状态帧或原生返回失去绑定，必须回收原 Job。");

            prepared = false;
            return result;
        }
        catch
        {
            failed = true;
            await DisarmAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DisarmAsync(CancellationToken token)
    {
        uint before = reader.U32(command + AfdevBioBotNavigationStopHook.IdleAck);
        var elapsed = Stopwatch.StartNew();

        process.WriteMemory(command, [0, 0, 0, 0]);

        while (reader.U32(command + AfdevBioBotNavigationStopHook.IdleAck) == before)
        {
            token.ThrowIfCancellationRequested();

            if (!process.IsAlive || elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("导航退休门没有取得游戏线程 IdleAck。");

            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }
}

internal sealed record AfdevBioBotProgramRetirement(uint Ordinal, uint Status, uint CallsStarted,
    uint CallsReturned, uint CancelTick, uint RetiredTick, uint Code, bool NativeStateChanged,
    uint Node, uint State, uint LatentAction);
