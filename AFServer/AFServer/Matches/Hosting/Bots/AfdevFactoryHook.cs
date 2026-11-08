using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;
/// <summary>
/// Accepts one Controller factory request for this world only; the native thread checks object identity and
/// ABI.
/// </summary>
static class FactoryHook
{
    public const uint ActorProcessEvent = 0x0075A9F0;
    public const int CommandSize = 0xB00;
    public const uint PreparationPage = 0x7B4;

    /// <summary>
    /// Appends this slot's one-time factory gate. All slots share the host's sole installed Tick entry.
    /// </summary>
    public static byte[] Append(byte[] wrapper, uint trace, uint command)
    {
        byte[] tail = TickHook.Build(0, trace).AsSpan(0xBC).ToArray();
        int insertion = wrapper.Length - tail.Length;

        if (insertion < 0 || !wrapper.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("Factory dispatcher requires the reviewed post-tick FX/GPR tail.");

        byte[] single = Build(0, trace, command);
        int bodyLength = single.Length - 0xBC - tail.Length;

        return [.. wrapper.AsSpan(0, insertion), .. single.AsSpan(0xBC, bodyLength), .. tail];
    }

    public static byte[] Build(uint codeAddress, uint traceAddress, uint commandAddress)
    {
        byte[] original = TickHook.Build(codeAddress, traceAddress);
        const int insertion = 0xBC; // Post-Tick floating-point and general-purpose registers are saved; insert before the original returned counter.

        if (!original.AsSpan(insertion, 3).SequenceEqual(Convert.FromHexString("F0FF05")))
            throw new InvalidDataException("Post-tick insertion anchor differs.");

        var asm = new Assembly(codeAddress + insertion);

        uint C(uint offset) => commandAddress + offset;
        void Load(uint address)
        {
            asm.Hex("A1");
            asm.Word(address);
        }

        void Store(uint address)
        {
            asm.Hex("A3");
            asm.Word(address);
        }

        void Set(uint address, uint value)
        {
            asm.Hex("C705");
            asm.Word(address);
            asm.Word(value);
        }

        void Cmp(uint address, uint value)
        {
            asm.Hex("813D");
            asm.Word(address);
            asm.Word(value);
        }

        void CmpEax(uint address)
        {
            asm.Hex("3B05");
            asm.Word(address);
        }

        void Jne(string label) => asm.Jcc(0x85, label);
        void Zero(string label)
        {
            asm.Hex("85C0");
            asm.Jcc(0x84, label);
        }

        void Thread(string failure, uint observedOffset)
        {
            asm.Hex("FF15");
            asm.Word(0x019F6228);
            Store(C(observedOffset));
            CmpEax(C(0x40));
            Jne(failure);
            Load(0x01FE22C0);
            CmpEax(C(0x40));
            Jne(failure);
            Cmp(0x01FE22C4, 1);
            Jne(failure);
        }

        void Gc(string failure)
        {
            Cmp(0x01FE229C, 0);
            Jne(failure);
            Cmp(0x01FD69E0, 0);
            Jne(failure);
            Cmp(0x02053E50, 0);
            Jne(failure);
            Cmp(0x02053E58, 0);
            Jne(failure);
        }

        void Objects(string failure)
        {
            // Matches native IsValid at 004E9790: signed index, nonempty slot, and back-reference checks.
            Load(C(0xC4));
            asm.Hex("83F840");
            asm.Jcc(0x87, failure);
            Zero(failure);
            asm.Hex("89C7BE");
            asm.Word(C(0x100));

            string loop = "objects_" + failure;

            asm.Label(loop);
            asm.Hex("8B06");
            Zero(failure);
            asm.Hex("8B560485D2");
            asm.Jcc(0x88, failure);
            asm.Hex("3B15");
            asm.Word(0x02053EAC);
            asm.Jcc(0x8D, failure);
            asm.Hex("8B0D");
            asm.Word(0x02053EA8);
            asm.Hex("85C9");
            asm.Jcc(0x84, failure);
            asm.Hex("390491");
            Jne(failure);
            foreach (var field in new[]
            {
                (4, 4),
                (0x28, 8),
                (0x2C, 12),
                (0x30, 16),
                (0x34, 20)
            }

            )
            {
                asm.Hex("8B50");
                asm.Hex(field.Item1.ToString("X2"));
                asm.Hex("3B56");
                asm.Hex(field.Item2.ToString("X2"));
                Jne(failure);
            }

            asm.Hex("F7400C00000020");
            Jne(failure);
            asm.Hex("83C6184F");
            Jne(loop);
        }

        void Identity(string failure, uint worldOffset, uint gameOffset, uint classOffset)
        {
            Load(0x02063820);
            CmpEax(C(0x44));
            Jne(failure);
            asm.Hex("83B87806000000");
            Jne(failure); // PendingURL.Count
            Load(0x02066BF8);
            Store(C(worldOffset));
            CmpEax(C(0x48));
            Jne(failure);
            asm.Hex("8B4050");
            Zero(failure);
            asm.Hex("83784000");
            asm.Jcc(0x8E, failure);
            asm.Hex("8B403C");
            Zero(failure);
            asm.Hex("8B00");
            CmpEax(C(0x4C));
            Jne(failure);
            asm.Hex("8B8014040000");
            Store(C(gameOffset));
            CmpEax(C(0x50));
            Jne(failure);
            asm.Hex("8B4034");
            Store(C(classOffset));
            CmpEax(C(0x54));
            Jne(failure);
        }

        void Preparing(string failure, string suffix)
        {
            Load(C(0x7AC));
            asm.Hex("83F840"); asm.Jcc(0x87, failure);
            Zero(failure);
            asm.Hex("89C7BE"); asm.Word(C(0x900));
            asm.Label("preparation_proofs_" + suffix);
            asm.Hex("8B068B003B4604"); Jne(failure);
            asm.Hex("83C6084F"); Jne("preparation_proofs_" + suffix);
            Load(C(0x50)); asm.Hex("8B4018"); Zero(failure);
            asm.Hex("8B4028"); CmpEax(C(0x780)); Jne(failure);
            Cmp(C(PreparationPage), 0); asm.Jcc(0x84, "empty_counts_" + suffix);
            asm.Bytes(AfdevBioBotPreparationHook.BuildGuard(C(PreparationPage), C(0x48), C(0x50)));
            Zero(failure);
            asm.Jmp("counts_checked_" + suffix);
            asm.Label("empty_counts_" + suffix);
            foreach (uint slot in new uint[] { 0x784, 0x788, 0x78C, 0x790 })
            {
                Load(C(slot)); asm.Hex("833800"); Jne(failure);
            }

            asm.Label("counts_checked_" + suffix);
            Load(C(0x794)); asm.Hex("833801"); asm.Jcc(0x8C, failure);
            Load(C(0x798)); asm.Hex("8B002305"); asm.Word(C(0x79C)); CmpEax(C(0x79C)); Jne(failure);
            Load(C(0x7A0)); asm.Hex("8B002305"); asm.Word(C(0x7A4)); Jne(failure);
            Load(C(0x7B0)); asm.Hex("803801"); Jne(failure);
            Load(C(0x48)); asm.Hex("8B80D8000000"); Zero(failure); CmpEax(C(0x7A8)); Jne(failure);
            // ClientConnections is TArray<UNetConnection*>. Driver/Connection bypass UObject identity decoding.
            Cmp(C(PreparationPage), 0); asm.Jcc(0x85, "connections_checked_" + suffix);
            asm.Hex("83784800"); Jne(failure);
            asm.Label("connections_checked_" + suffix);
            asm.Hex("83784C40"); asm.Jcc(0x87, failure);
        }

        Cmp(C(0), 1);
        Jne("done");
        Cmp(C(4), 0);
        Jne("done");
        Cmp(C(8), 0);
        Jne("done");
        Thread("thread", 0x18);
        Gc("gc");
        Cmp(traceAddress + 0x28, 2);
        Jne("identity");
        Load(traceAddress + 8);
        CmpEax(C(0x48));
        Jne("identity");
        Identity("identity", 0x20, 0x28, 0x38);
        Objects("objects_before");
        Preparing("preparation", "before");
        Load(C(0x50));
        asm.Hex("F780B000000008000000");
        Jne("identity");
        Load(C(0x58));
        asm.Hex("8B5034"); // UFunction.Class
        asm.Hex("3B15");
        asm.Word(C(0x60));
        Jne("function");
        asm.Hex("8B5028"); // UFunction.Owner
        asm.Hex("3B15");
        asm.Word(C(0x64));
        Jne("function");
        asm.Hex("81B8AC00000002000200");
        Jne("function"); // FUNC_Defined | FUNC_Public
        asm.Hex("6683B8BE0000001C");
        Jne("function");
        asm.Hex("6683B8C000000018");
        Jne("function");
        asm.Hex("6683B8B000000000");
        Jne("function");
        asm.Hex("8378501C");
        Jne("function");
        foreach (var field in new[]
        {
            (0xC8, 0x80u),
            (0x54, 0x84u),
            (0x58, 0x88u),
            (0x5C, 0x8Cu),
            (0x4C, 0x90u),
            (0x2C, 0xA0u),
            (0x30, 0xA4u),
            (0x04, 0xA8u)
        }

        )
        {
            asm.Hex("8B90");
            asm.Word((uint)field.Item1);
            asm.Hex("3B15");
            asm.Word(C(field.Item2));
            Jne("function");
        }

        for (uint index = 0; index < 3; index++)
        {
            uint block = 0x700 + index * 32;

            Load(C(block));
            foreach (var field in new[]
            {
                (0x3C, 4u),
                (0x40, 8u),
                (0x44, 12u),
                (0x48, 16u),
                (0x4C, 20u),
                (0x60, 24u),
                (0x84, 28u)
            }

            )
            {
                asm.Hex("8B90");
                asm.Word((uint)field.Item1);
                asm.Hex("3B15");
                asm.Word(C(block + field.Item2));
                Jne("function");
            }
        }

        Load(C(0xE0));
        asm.Hex("8B00");
        CmpEax(C(0xE4));
        Jne("function");
        Load(C(0xE8));
        asm.Hex("8B00");
        CmpEax(C(0xEC));
        Jne("function");
        Load(C(0x50));
        asm.Hex("8B00"); // receiver vtable
        asm.Hex("81B820010000");
        asm.Word(ActorProcessEvent);
        Jne("function");
        Load(C(0x5C));
        asm.Hex("837818FF");
        Jne("function"); // poison must remain -1
        Set(C(0x10), 0);
        Set(C(4), 1); // Record started before the actual function call. A timeout must not create a second request.
        asm.Hex("8925");
        asm.Word(C(0x68)); // ESP before three ABI arguments。
        asm.Hex("6A00FF35");
        asm.Word(C(0x5C));
        asm.Hex("FF35");
        asm.Word(C(0x58));
        asm.Hex("8B0D");
        asm.Word(C(0x50));
        asm.Hex("8B01FF9020010000"); // Native Actor ProcessEvent(this, function, parms, 0), ret 0x0C.
        asm.Hex("8925");
        asm.Word(C(0x6C));
        Load(C(0x5C));
        asm.Hex("8B4018");
        Store(C(0x30));
        Load(C(0x68));
        CmpEax(C(0x6C));
        Jne("changed");
        Thread("changed", 0x1C);
        Gc("changed");
        Identity("changed", 0x24, 0x2C, 0x3C);
        Objects("objects_after");
        Preparing("changed", "after");
        Set(C(0x0C), 1);
        asm.Jmp("complete");
        asm.Label("changed");
        Set(C(0x0C), 2);
        asm.Label("complete");
        Set(C(8), 1); // Mark completion only after ProcessEvent actually returns.
        asm.Jmp("done");
        asm.Label("objects_after");
        asm.Jmp("changed");
        foreach (var item in new[]
        {
            ("thread", 1u),
            ("gc", 2u),
            ("identity", 3u),
            ("function", 4u),
            ("objects_before", 5u),
            ("preparation", 6u)
        }

        )
        {
            asm.Label(item.Item1);
            Set(C(0x10), item.Item2);
            asm.Hex("F0FF05");
            asm.Word(C(0x14));
            asm.Jmp("done");
        }

        asm.Label("done");

        byte[] query = asm.Finish();

        return[..original.AsSpan(0, insertion).ToArray(), ..query, ..original.AsSpan(insertion).ToArray()];
    }

    sealed class Assembly(uint start)
    {
        private readonly List<byte> code = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Offset, string Label)> branches = [];
        public void Hex(string hex) => code.AddRange(Convert.FromHexString(hex));
        public void Bytes(byte[] bytes) => code.AddRange(bytes);
        public void Word(uint value)
        {
            byte[] bytes = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            code.AddRange(bytes);
        }

        public void Label(string name) => labels.Add(name, code.Count);
        public void Jcc(byte condition, string name)
        {
            code.Add(0x0F);
            code.Add(condition);
            Branch(name);
        }

        public void Jmp(string name)
        {
            code.Add(0xE9);
            Branch(name);
        }

        private void Branch(string name)
        {
            branches.Add((code.Count, name));
            Word(0);
        }

        public byte[] Finish()
        {
            byte[] result = code.ToArray();

            foreach (var branch in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch.Offset, 4), labels[branch.Label] - branch.Offset - 4);
            _ = start; // Branches use section-relative addresses; absolute references are relocated to the command page.
            return result;
        }
    }
}
