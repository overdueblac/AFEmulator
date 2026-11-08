using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Pawn physics virtual-slot router for the same owned process. Install each static vtable once and retain
/// code pages until the original Job stops. Bot actions publish/retire only their own command pages, never
/// restoring shared virtual slots or disrupting other Pawns' native physics.
/// </summary>
internal sealed class AfdevBioBotPhysicsObservers
{
    private readonly AfdevOwnedProcess process;
    private readonly uint trace;
    private readonly uint[] commands;
    private readonly object gate = new();
    private readonly Dictionary<uint, AfdevBioBotPhysicsObserver> observers = [];
    private readonly Dictionary<uint, Binding> bindings = [];

    public AfdevBioBotPhysicsObservers(AfdevOwnedProcess process, uint trace, IReadOnlyList<uint> commands)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(commands);

        if (trace < 0x10000 || commands.Count is 0 or > 15 ||
            commands.Distinct().Count() != commands.Count ||
            commands.Any(command => command < 0x10000 || (command & 3) != 0))
            throw new ArgumentException("物理观察器必须持有本实例全部独立运动槽。", nameof(commands));

        this.process = process;
        this.trace = trace;
        this.commands = commands.ToArray();
    }

    /// <summary>
    /// Installs the router on first encounter with a validated Pawn class. Later actions reuse that class's
    /// retained original entry and router address.
    /// </summary>
    public AfdevBioBotPhysicsObserver Resolve(uint command, QueryObject pawn) => Paused(command, reader =>
    {
        if (reader.Identity(pawn.Address) != pawn)
            throw new AfdevBioBotActionInterruptedException("安装物理观察器前 Pawn 身份已更换。");

        uint vtable = reader.U32(pawn.Address);

        if (vtable < 0x00400000 || vtable >= 0x03000000 - 0x230)
            throw new InvalidDataException("物理观察器不接受动态或未知的 Pawn 虚表。");

        uint current = reader.U32(vtable + 0x22C);

        if (observers.TryGetValue(vtable, out AfdevBioBotPhysicsObserver? known))
        {
            if (current != known.Entry)
                throw new InvalidDataException("共享物理虚槽已被其他工作改变；必须回收原 Job。");

            return known;
        }

        if (current < 0x00400000 || current >= 0x03000000)
            throw new InvalidDataException("首次安装物理观察器时原生虚函数已被替换。");

        byte[] code = AfdevBioBotMotionHook.BuildPhysicsObserver(commands, trace, current);
        uint entry = process.AllocateMemory(code.Length, executable: true);

        process.WriteMemory(entry, code);

        if (!process.ReadMemory(entry, code.Length).AsSpan().SequenceEqual(code))
            throw new IOException("共享物理观察器代码回读失败；必须回收原 Job。");

        var observer = new AfdevBioBotPhysicsObserver(vtable, current, entry, code);

        // Register before writing the pointer so later checks can identify uncertain writes. Never overwrite another observer.
        observers.Add(vtable, observer);
        WriteWord(vtable + 0x22C, entry);

        if (reader.U32(vtable + 0x22C) != entry)
            throw new IOException("共享物理观察器虚槽回读失败；必须回收原 Job。");

        return observer;
    });

    /// <summary>
    /// The caller first obtains this slot's IdleAck. Pause the game thread, write/read back the complete
    /// descriptor, then publish its independent observation bit. The resident router must not read partial
    /// identities or alter diagnostics during Mode0 parameter reuse.
    /// </summary>
    public void Publish(uint command, QueryObject pawn, AfdevBioBotPhysicsObserver observer, byte[] payload) =>
        Paused(command, reader =>
        {
            if (payload.Length != AfdevBioBotMotionHook.CommandSize || bindings.ContainsKey(command) ||
                bindings.Values.Any(binding => binding.Pawn.Address == pawn.Address) ||
                reader.U32(command + AfdevBioBotMotionHook.ObservationPublished) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(payload) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan((int)AfdevBioBotMotionHook.ObservationPublished)) != 0)
                throw new InvalidDataException("运动槽已有观察绑定或描述尚未停发，不能重新发布。");

            RequireIdle(reader, command);

            if (reader.Identity(pawn.Address) != pawn || reader.U32(pawn.Address) != observer.Vtable)
                throw new AfdevBioBotActionInterruptedException("发布物理观察绑定前 Pawn 已更换。");
            if (!observers.TryGetValue(observer.Vtable, out AfdevBioBotPhysicsObserver? known) || known != observer ||
                reader.U32(observer.Vtable + 0x22C) != observer.Entry)
                throw new InvalidDataException("运动槽失去本实例的共享物理路由。");

            byte[] descriptor = (byte[])payload.Clone();

            // Natural physics may already have run in the new-binding Tick without this slot observing it. Do not add integration for that Tick,
            // and do not fabricate ActualPhysicsEntries or LastObservedPhysicsTick evidence.
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan((int)AfdevBioBotMotionHook.BindingTick), reader.U32(trace));
            process.WriteMemory(command, descriptor);

            if (!process.ReadMemory(command, descriptor.Length).AsSpan().SequenceEqual(descriptor))
                throw new IOException("运动命令完整描述回读失败；必须回收原 Job。");

            bindings.Add(command, new Binding(pawn, observer));
            WriteWord(command + AfdevBioBotMotionHook.ObservationPublished, 1);

            if (reader.U32(command + AfdevBioBotMotionHook.ObservationPublished) != 1)
                throw new IOException("运动观察绑定发布回读失败；必须回收原 Job。");

            return true;
        });

    /// <summary>
    /// Stop publication and confirm all slot calls returned before retiring the observation binding. The
    /// shared virtual slot keeps forwarding the original function. Cross another IdleAck before reusing
    /// parameters or Pawn identity; if return cannot be confirmed, retain the page and reclaim the original
    /// Job.
    /// </summary>
    public void Retire(uint command) => Paused(command, reader =>
    {
        if (!bindings.TryGetValue(command, out Binding? binding))
        {
            if (reader.U32(command + AfdevBioBotMotionHook.ObservationPublished) != 0)
                throw new InvalidDataException("运动槽存在未归本实例登记的观察绑定。");

            return false;
        }

        RequireIdle(reader, command);

        if (reader.U32(binding.Observer.Vtable + 0x22C) != binding.Observer.Entry)
            throw new InvalidDataException("退休运动槽时共享物理虚槽已改变；必须回收原 Job。");

        WriteWord(command + AfdevBioBotMotionHook.ObservationPublished, 0);

        if (reader.U32(command + AfdevBioBotMotionHook.ObservationPublished) != 0)
            throw new IOException("运动观察绑定退休回读失败；必须回收原 Job。");

        bindings.Remove(command);
        return true;
    });

    private T Paused<T>(uint command, Func<QueryInspector, T> action)
    {
        lock (gate)
        {
            if (!commands.Contains(command) || !process.IsAlive)
                throw new InvalidOperationException("物理观察器失去自有进程或本槽所有权。");

            process.SuspendMainThread();

            try
            {
                return action(new QueryInspector(process));
            }
            finally
            {
                process.ResumeMainThread();
            }
        }
    }

    private static void RequireIdle(QueryInspector reader, uint command)
    {
        if (reader.U32(command) != 0 || reader.U32(command + AfdevBioBotMotionHook.PhysicsInFlight) != 0 ||
            reader.U32(command + 8) != reader.U32(command + 12) ||
            reader.U32(command + 0x28) != reader.U32(command + AfdevBioBotMotionHook.ActualPhysicsReturns))
            throw new InvalidDataException("本槽物理调用仍活动或返回未知；必须回收原 Job。");
    }

    private void WriteWord(uint address, uint value)
    {
        byte[] bytes = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        process.WriteMemory(address, bytes);
    }

    private sealed record Binding(QueryObject Pawn, AfdevBioBotPhysicsObserver Observer);
}

internal sealed record AfdevBioBotPhysicsObserver(uint Vtable, uint Original, uint Entry, byte[] Code);
