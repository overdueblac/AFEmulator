using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Independent motion-command page. The assembler generates machine code only; the same host retains
/// installation and publication ownership.
/// </summary>
internal static class AfdevBioBotMotionHook
{
    public const int CommandSize = 0x1000;
    public const uint IdleAck = 0x1C;
    public const uint Parameters = 0xD00;
    public const uint ActualPhysicsReturns = 0x34;
    public const uint NaturalPhysicsReturns = 0x38;
    public const uint DrivenPhysicsEntries = 0x3C;
    public const uint DrivenPhysicsReturns = 0xC0;
    public const uint LastPhysicsCaller = 0xC4;
    public const uint LastNaturalPhysicsCaller = 0xC8;
    public const uint LastDrivenPhysicsCaller = 0xCC;
    public const uint RepeatedPhysicsEntries = 0xD0;
    public const uint PhysicsInFlight = 0xD4;
    public const uint LastPhysicsReturnTick = 0xD8;
    public const uint LastObservedPhysicsTick = 0xDC;
    public const uint PawnOuter = 0xE0;
    public const uint PawnNameIndex = 0xE4;
    public const uint PawnNameNumber = 0xE8;
    public const uint NativeCallReceiver = 0xEC;
    public const uint NativeCallDeltaBits = 0xF0;
    public const uint NativeCallStackBefore = 0xF4;
    public const uint NativeCallStackAfter = 0xF8;
    public const uint ObservationPublished = 0xFC;
    public const uint BindingTick = 0x100;
    public const uint ExpectedLatent = 0x104;

    /// <summary>
    /// Physics virtual-slot observer. Unbound Pawn calls forward unchanged; bound calls record actual entry
    /// and normal return separately. Current PH +0x22C accepts one float and callee-cleans four bytes. Copy
    /// that argument to the original virtual call, preserve its resulting registers, flags, and
    /// floating-point state, then ret 4 to finish the original call.
    /// </summary>
    public static byte[] BuildPhysicsObserver(uint command, uint trace, uint original) =>
        BuildPhysicsObserver([command], trace, original);

    /// <summary>
    /// Installs only this shared router for a Pawn class's virtual slot. Each command page publishes a
    /// complete independent identity before dispatch by exact Pawn. Stopping or retiring one slot leaves
    /// others intact; unmatched calls forward to the native function exactly once.
    /// </summary>
    public static byte[] BuildPhysicsObserver(IReadOnlyList<uint> commands, uint trace, uint original)
    {
        if (commands.Count is 0 or > 15 || commands.Distinct().Count() != commands.Count ||
            commands.Any(command => command < 0x10000 || (command & 3) != 0) ||
            original < 0x00400000 || original >= 0x03000000)
            throw new ArgumentException("物理观察路由缺少独立命令槽或已核验的原生虚函数。");

        var a = new AfdevMotionAssembly();

        a.Hex("9C5052");

        for (int index = 0; index < commands.Count; index++)
        {
            uint command = commands[index];
            string next = "next-" + index;
            string newTick = "new-tick-" + index;
            string natural = "natural-" + index;
            string returned = "returned-" + index;

            a.Compare(command + ObservationPublished, 1); a.Jcc(0x85, next);
            a.Hex("3B0D"); a.Word(command + 0x4C); a.Jcc(0x85, next);
            a.Hex("8B4104"); a.Equal(command + 0xB4, "forward");
            a.Hex("3B05"); a.Word(0x02053EAC); a.Jcc(0x83, "forward");
            a.Hex("8B15"); a.Word(0x02053EA8); a.Hex("85D2"); a.Jcc(0x84, "forward");
            a.Hex("390C82"); a.Jcc(0x85, "forward");
            a.Hex("8B4134"); a.Equal(command + 0xB8, "forward");
            a.Hex("8B4128"); a.Equal(command + PawnOuter, "forward");
            a.Hex("8B412C"); a.Equal(command + PawnNameIndex, "forward");
            a.Hex("8B4130"); a.Equal(command + PawnNameNumber, "forward");
            a.Hex("F7410C00000020"); a.Jcc(0x85, "forward");
            a.Hex("F781B000000008000000"); a.Jcc(0x85, "forward");
            a.Load(0x02066BF8); a.Equal(command + 0x40, "forward");
            a.Load(command + 0x7C); a.Hex("8B0039C8"); a.Jcc(0x85, "forward");
            a.Load(command + 0x80); a.Hex("8B00"); a.Equal(command + 0x48, "forward");
            a.Load(command + 0x84); a.Hex("8B00"); a.Equal(command + 0x44, "forward");
            a.Load(command + 0x8C); a.Hex("833800"); a.Jcc(0x85, "forward");
            a.Load(command + 0x90); a.Hex("8B002305"); a.Word(command + 0x94); a.Jcc(0x84, "forward");
            // Retain observed natural physics Ticks during Mode0 handoff. Stop accessing this page only after its independent publication bit retires.
            a.Load(trace); a.Store(command + LastObservedPhysicsTick);
            a.Load(command); a.Hex("85C0"); a.Jcc(0x84, "forward");
            a.Load(trace); a.Equal(command + 0x24, newTick);
            a.Compare(command + 0x28, 0); a.Jcc(0x84, newTick);
            a.Increment(command + RepeatedPhysicsEntries);
            a.Label(newTick);
            a.Load(trace); a.Store(command + 0x24);
            a.Increment(command + 0x28);
            a.Increment(command + PhysicsInFlight);
            a.Hex("8B44240C"); a.Store(command + LastPhysicsCaller);
            // Require both execution of the owned native physics body and the actual call site. Other native entries remain natural physics.
            a.Compare(command + 0x30, 1); a.Jcc(0x85, natural);
            a.Hex("3D"); a.Word(AfdevBioBotMotionFunctions.AutonomousPhysicsDispatchReturn); a.Jcc(0x85, natural);
            a.Store(command + LastDrivenPhysicsCaller);
            a.Increment(command + DrivenPhysicsEntries);
            a.Hex("5A589DFF742404"); a.Call("native");
            a.Hex("9C");
            a.Increment(command + ActualPhysicsReturns);
            a.Increment(command + DrivenPhysicsReturns);
            a.Jmp(returned);
            a.Label(natural);
            a.Store(command + LastNaturalPhysicsCaller);
            a.Increment(command + 0x2C);
            a.Hex("5A589DFF742404"); a.Call("native");
            a.Hex("9C");
            a.Increment(command + ActualPhysicsReturns);
            a.Increment(command + NaturalPhysicsReturns);
            a.Label(returned);
            a.Hex("FF0D"); a.Word(command + PhysicsInFlight);
            a.Hex("50"); a.Load(trace); a.Store(command + LastPhysicsReturnTick); a.Hex("589DC20400");
            a.Label(next);
        }

        a.Label("forward");
        a.Hex("5A589D68"); a.Word(original); a.Hex("C3");
        a.Label("native");
        a.Hex("68"); a.Word(original); a.Hex("C3");
        return a.Finish();
    }

    public static byte[] Append(byte[] wrapper, uint trace, uint factoryCommand, uint command)
    {
        byte[] tail = TickHook.Build(0, trace).AsSpan(0xBC).ToArray();
        int insertion = wrapper.Length - tail.Length;

        if (insertion < 0 || !wrapper.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("运动执行器缺少已审核的 Tick 尾部。");

        var a = new AfdevMotionAssembly();

        uint C(uint offset) => command + offset;
        void Compare(uint address, uint value) => a.Compare(address, value);
        void Equal(uint address) => a.Equal(address, "failed");
        void Nonzero() { a.Hex("85C0"); a.Jcc(0x84, "failed"); }
        void IndirectEqual(uint address, uint expected)
        {
            a.Load(address); a.Hex("8B00"); Equal(expected);
        }

        a.Load(C(0)); a.Hex("85C0"); a.Jcc(0x85, "active");
        a.Increment(C(IdleAck)); a.Jmp("done");
        a.Label("active");
        Compare(C(4), 0); a.Jcc(0x85, "done");
        a.Hex("83F803"); a.Jcc(0x87, "failed");
        Compare(C(ObservationPublished), 1); a.Jcc(0x85, "failed");
        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
        {
            Compare(address, 0); a.Jcc(0x85, "done");
        }

        a.Hex("F705"); a.Word(0x01FCE998); a.Word(1); a.Jcc(0x85, "failed");
        a.Hex("FF15"); a.Word(0x019F6228); Equal(C(0x60));
        a.Load(0x01FE22C0); Equal(C(0x60));
        Compare(0x01FE22C4, 1); a.Jcc(0x85, "failed");
        a.Load(0x02063820); Equal(C(0x64));
        a.Hex("83B87806000000"); a.Jcc(0x85, "failed");
        a.Load(0x02066BF8); Equal(C(0x40));
        a.Hex("8B4050"); Nonzero(); a.Hex("83784000"); a.Jcc(0x8E, "failed");
        a.Hex("8B403C"); Nonzero(); a.Hex("8B00"); Equal(C(0x44));
        a.Hex("8B8014040000"); Equal(C(0x68));
        Compare(factoryCommand + 8, 1); a.Jcc(0x85, "failed");
        Compare(factoryCommand + 12, 1); a.Jcc(0x85, "failed");
        a.Load(factoryCommand + 0x30); Equal(C(0x48));

        a.Load(C(0x6C)); a.Hex("83F840"); a.Jcc(0x87, "failed"); Nonzero();
        a.Hex("89C7BE"); a.Word(C(0x200));
        a.Label("identities");
        a.Hex("8B068B560485D2"); a.Jcc(0x88, "failed");
        a.Hex("3B15"); a.Word(0x02053EAC); a.Jcc(0x8D, "failed");
        a.Hex("8B0D"); a.Word(0x02053EA8); a.Hex("85C9"); a.Jcc(0x84, "failed");
        a.Hex("390491"); a.Jcc(0x85, "failed");
        foreach (var item in new[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
        {
            a.Hex("8B50" + item.Item1.ToString("X2") + "3B56" + item.Item2.ToString("X2"));
            a.Jcc(0x85, "failed");
        }

        a.Hex("F7400C00000020"); a.Jcc(0x85, "failed");
        a.Hex("83C6184F"); a.Jcc(0x85, "identities");
        a.Load(C(0x70)); a.Hex("83F850"); a.Jcc(0x87, "failed"); Nonzero();
        a.Hex("89C7BE"); a.Word(C(0x900));
        a.Label("guards");
        a.Hex("8B068B002346083B4604"); a.Jcc(0x85, "failed");
        a.Hex("83C60C4F"); a.Jcc(0x85, "guards");

        a.Load(C(0x48)); a.Hex("F780B000000008000000"); a.Jcc(0x85, "failed");
        a.Load(C(0x4C)); a.Hex("F780B000000008000000"); a.Jcc(0x85, "failed");
        IndirectEqual(C(0x7C), C(0x4C));
        IndirectEqual(C(0x80), C(0x48));
        IndirectEqual(C(0x84), C(0x44));
        a.Load(C(0x88)); a.Hex("833800"); a.Jcc(0x8E, "failed");
        a.Load(C(0x8C)); a.Hex("833800"); a.Jcc(0x85, "failed");
        a.Load(C(0x90)); a.Hex("8B002305"); a.Word(C(0x94)); Nonzero();
        a.Load(C(0x4C)); a.Hex("8B00"); Equal(C(0xA0));
        a.Hex("8B802C020000"); Equal(C(0xA8));
        a.Load(C(0x48)); a.Hex("8B4018"); Equal(C(0x50)); Nonzero();
        a.Hex("8B5010"); a.Hex("8915"); a.Word(C(0xBC));
        a.Hex("8B4014"); Equal(C(0x48));
        a.Load(C(0xBC)); Equal(C(0x54));
        a.Load(C(0x50)); a.Hex("8B4028"); Equal(C(0x58));
        a.Load(C(0x50)); a.Hex("8B4018"); a.Hex("85C0"); a.Jcc(0x84, "finished");
        Equal(C(0x5C));
        Compare(C(0), 2); a.Jcc(0x84, "cancel");
        a.Load(C(0x50)); a.Hex("0FB74030"); Equal(C(ExpectedLatent));
        Compare(C(0), 3); a.Jcc(0x84, "done"); // Sample actual physics entry only; do not initiate integration.
        a.Load(C(0x74)); a.Hex("8B002305"); a.Word(C(0x78)); a.Hex("85C0"); a.Jcc(0x84, "done");
        // The native observer covers world physics calls. Never integrate again when the same Tick already did so.
        a.Load(trace); a.Hex("3B05"); a.Word(C(BindingTick)); a.Jcc(0x84, "done");
        a.Hex("3B05"); a.Word(C(LastObservedPhysicsTick)); a.Jcc(0x84, "done");
        a.Hex("3B05"); a.Word(C(0x20)); a.Jcc(0x84, "done");
        a.Store(C(0x20));
        a.Load(trace + 0x2C); a.Hex("85C0"); a.Jcc(0x8E, "failed");
        a.Hex("3D0000803E"); a.Jcc(0x87, "failed"); // Require 0 < delta <= 0.25; reject negative values, NaN, and infinity.
        a.Store(C(Parameters));
        a.Store(C(NativeCallDeltaBits));
        a.Hex(Convert.ToHexString(AfdevBioBotMovementInput.Build(command)));
        a.Load(C(0x4C)); a.Store(C(NativeCallReceiver));
        a.Set(C(0x30), 1); a.Increment(C(8));
        // ProcessEvent rejects native3971 directly at 0x483598. Call the fully verified original body:
        // ECX is the same-generation Pawn, with one actual-world float on the stack, removed by the body's ret 4. Preserve all UFunction metadata.
        a.Hex("8925"); a.Word(C(NativeCallStackBefore));
        a.Hex("FF35"); a.Word(C(Parameters));
        a.Hex("8B0D"); a.Word(C(0x4C));
        a.Hex("B8"); a.Word(AfdevBioBotMotionFunctions.AutonomousPhysicsBody); a.Hex("FFD0");
        a.Hex("8925"); a.Word(C(NativeCallStackAfter));
        a.Set(C(0x30), 0); a.Increment(C(12));
        a.Load(C(NativeCallStackBefore)); Equal(C(NativeCallStackAfter)); a.Jmp("done");
        a.Label("cancel");
        Compare(C(0x10), 0); a.Jcc(0x85, "done");
        a.Load(C(0x50)); a.Hex("0FB74030"); Equal(C(ExpectedLatent));
        a.Increment(C(0x10));
        a.Hex("6A0068"); a.Word(C(Parameters));
        a.Hex("FF35"); a.Word(C(0x9C));
        a.Hex("8B0D"); a.Word(C(0x48));
        a.Hex("B8"); a.Word(QueryHook.ActorProcessEvent); a.Hex("FFD0");
        a.Increment(C(0x14));
        a.Load(C(0x50)); a.Hex("6683783000"); a.Jcc(0x85, "failed");
        a.Jmp("done");
        a.Label("finished");
        a.Load(C(0x50)); a.Hex("6683783000"); a.Jcc(0x85, "failed");
        a.Set(C(4), 1); a.Jmp("done");
        a.Label("failed"); a.Set(C(4), 3);
        a.Label("done");
        return [.. wrapper.AsSpan(0, insertion).ToArray(), .. a.Finish(), .. tail];
    }
}

internal sealed class AfdevMotionAssembly
{
    private readonly List<byte> code = [];
    private readonly Dictionary<string, int> labels = [];
    private readonly List<(int Offset, string Label)> branches = [];
    public void Hex(string hex) => code.AddRange(Convert.FromHexString(hex));
    public void Word(uint value)
    {
        byte[] bytes = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        code.AddRange(bytes);
    }

    public void Load(uint address) { Hex("A1"); Word(address); }
    public void Store(uint address) { Hex("A3"); Word(address); }
    public void Set(uint address, uint value) { Hex("C705"); Word(address); Word(value); }
    public void Compare(uint address, uint value) { Hex("813D"); Word(address); Word(value); }
    public void Equal(uint address, string failure) { Hex("3B05"); Word(address); Jcc(0x85, failure); }
    public void Increment(uint address) { Hex("FF05"); Word(address); }
    public void Label(string label) => labels.Add(label, code.Count);
    public void Jcc(byte condition, string label) { Hex("0F" + condition.ToString("X2")); Branch(label); }
    public void Jmp(string label) { Hex("E9"); Branch(label); }
    public void Call(string label) { Hex("E8"); Branch(label); }
    private void Branch(string label) { branches.Add((code.Count, label)); Word(0); }
    public byte[] Finish()
    {
        byte[] bytes = code.ToArray();

        foreach (var branch in branches)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(branch.Offset, 4), labels[branch.Label] - branch.Offset - 4);

        return bytes;
    }
}
