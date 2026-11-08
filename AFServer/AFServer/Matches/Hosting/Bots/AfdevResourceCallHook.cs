using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;
/// <summary>
/// Initializes factory PRI identity, default profile, and synchronization arrays in order within one
/// native-thread gate.
/// </summary>
static class ResourceCallHook
{
    public const int CommandSize = 0xC00;
    public const uint OrdinalOffset = 0xF00;
    public const uint IdleAckOffset = 0xF04;
    public const uint PreparationPage = 0xF8;
    public static byte[] Build(uint code, uint trace, uint factoryCommand, uint command) => Append(FactoryHook.Build(code, trace, factoryCommand), trace, factoryCommand, command);
    public static byte[] Append(byte[] wrapper, uint trace, uint factoryCommand, uint command)
    {
        byte[] tail = TickHook.Build(0, trace).AsSpan(0xBC).ToArray();
        int insertion = wrapper.Length - tail.Length;

        if (insertion < 0 || !wrapper.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("Resource dispatcher requires the reviewed post-tick FX/GPR tail.");

        var a = new Assembly();

        uint C(uint offset) => command + offset;
        void Load(uint address)
        {
            a.Hex("A1");
            a.Word(address);
        }

        void Store(uint address)
        {
            a.Hex("A3");
            a.Word(address);
        }

        void Set(uint address, uint value)
        {
            a.Hex("C705");
            a.Word(address);
            a.Word(value);
        }

        void Cmp(uint address, uint value)
        {
            a.Hex("813D");
            a.Word(address);
            a.Word(value);
        }

        void Eq(uint address, string failure)
        {
            a.Hex("3B05");
            a.Word(address);
            a.Jcc(0x85, failure);
        }

        void Ne(string failure) => a.Jcc(0x85, failure);
        void Zero(string failure)
        {
            a.Hex("85C0");
            a.Jcc(0x84, failure);
        }

        void Thread(string failure, uint destination)
        {
            a.Hex("FF15");
            a.Word(0x019F6228);
            Store(C(destination));
            Eq(C(0x40), failure);
            Load(0x01FE22C0);
            Eq(C(0x40), failure);
            Cmp(0x01FE22C4, 1);
            Ne(failure);
        }

        void Gc(string failure)
        {
            foreach (uint address in new uint[]
            {
                0x01FE229C,
                0x01FD69E0,
                0x02053E50,
                0x02053E58
            }

            )
            {
                Cmp(address, 0);
                Ne(failure);
            }
        }

        void Pair(string failure, uint worldObserved, uint gameObserved, uint classObserved)
        {
            Load(0x02063820);
            Eq(C(0x44), failure);
            a.Hex("83B87806000000");
            Ne(failure);
            Load(0x02066BF8);
            Store(C(worldObserved));
            Eq(C(0x48), failure);
            a.Hex("8B4050");
            Zero(failure);
            a.Hex("83784000");
            a.Jcc(0x8E, failure);
            a.Hex("8B403C");
            Zero(failure);
            a.Hex("8B00");
            Eq(C(0x4C), failure);
            a.Hex("8B8014040000");
            Store(C(gameObserved));
            Eq(C(0x50), failure);
            a.Hex("8B4034");
            Store(C(classObserved));
            Eq(C(0x54), failure);
            foreach (var link in new[]
            {
                (0xC8u, 0x74u),
                (0xCCu, 0x70u),
                (0xD0u, 0x4Cu),
                (0xD4u, 0x4Cu)
            }

            )
            {
                Load(C(link.Item1));
                a.Hex("8B00");
                Eq(C(link.Item2), failure);
            }

            foreach (uint pointer in new uint[]
            {
                0xD8,
                0xDC
            }

            )
            {
                Load(C(pointer));
                a.Hex("833800");
                Ne(failure);
            }

            foreach (uint pointer in new uint[]
            {
                0x70,
                0x74
            }

            )
            {
                Load(C(pointer));
                a.Hex("F780B000000008000000");
                Ne(failure);
            }

            Cmp(C(PreparationPage), 0); a.Jcc(0x84, "preparation_checked_" + failure);
            a.Bytes(AfdevBioBotPreparationHook.BuildGuard(C(PreparationPage), C(0x48), C(0x50)));
            Zero(failure);
            a.Label("preparation_checked_" + failure);
        }

        void Identities(string failure)
        {
            Load(C(0xC4));
            a.Hex("83F820");
            a.Jcc(0x87, failure);
            Zero(failure);
            a.Hex("89C7BE");
            a.Word(C(0x100));

            string loop = "identity_loop_" + failure;

            a.Label(loop);
            a.Hex("8B06");
            Zero(failure);
            a.Hex("8B560485D2");
            a.Jcc(0x88, failure);
            a.Hex("3B15");
            a.Word(0x02053EAC);
            a.Jcc(0x8D, failure);
            a.Hex("8B0D");
            a.Word(0x02053EA8);
            a.Hex("85C9");
            a.Jcc(0x84, failure);
            a.Hex("390491");
            Ne(failure);
            foreach (var f in new[]
            {
                (4, 4),
                (0x28, 8),
                (0x2C, 12),
                (0x30, 16),
                (0x34, 20)
            }

            )
            {
                a.Hex("8B50");
                a.Hex(f.Item1.ToString("X2"));
                a.Hex("3B56");
                a.Hex(f.Item2.ToString("X2"));
                Ne(failure);
            }

            a.Hex("F7400C00000020");
            Ne(failure);
            a.Hex("83C6184F");
            Ne(loop);
        }

        void Function(uint flags, uint propertiesSize, uint parmsSize, uint returnOffset, uint scriptSize)
        {
            Load(C(0x58));
            a.Hex("81B8AC000000");
            a.Word(flags);
            Ne("metadata");
            a.Hex("6683B8B000000000");
            Ne("metadata");
            a.Hex("817850");
            a.Word(propertiesSize);
            Ne("metadata");
            a.Hex("6681B8BE000000");
            a.Half((ushort)parmsSize);
            Ne("metadata");
            a.Hex("6681B8C0000000");
            a.Half((ushort)returnOffset);
            Ne("metadata");
            a.Hex("817858");
            a.Word(scriptSize);
            Ne("metadata");
            a.Hex("81785C");
            a.Word(scriptSize);
            Ne("metadata");
        }

        void Property(uint flags, uint size, bool boolean)
        {
            Load(C(0xB0));
            Zero("metadata");
            foreach (var f in new[]
            {
                (0x3C, 0u),
                (0x40, 1u),
                (0x44, size),
                (0x48, flags),
                (0x4C, 0u),
                (0x60, 0u)
            }

            )
            {
                a.Hex("81B8");
                a.Word((uint)f.Item1);
                a.Word(f.Item2);
                Ne("metadata");
            }

            a.Hex("8B5028");
            a.Hex("3B15");
            a.Word(C(0x58));
            Ne("metadata");
            if (boolean)
            {
                a.Hex("83B88400000001");
                Ne("metadata");
            }
        }

        Cmp(C(0), 1);
        a.Jcc(0x84, "requested");
        a.Hex("F0FF05");
        a.Word(C(IdleAckOffset));
        a.Jmp("done");
        a.Label("requested");
        Cmp(C(4), 0);
        Ne("done");
        Cmp(C(8), 0);
        Ne("done");
        Load(C(OrdinalOffset));
        a.Hex("83F803");
        a.Jcc(0x83, "sequence");
        a.Hex("40");
        Eq(C(0xAC), "sequence");
        Cmp(factoryCommand + 8, 1);
        Ne("sequence");
        Cmp(factoryCommand + 0x0C, 1);
        Ne("sequence");
        Load(factoryCommand + 0x30);
        Eq(C(0x70), "sequence");
        Thread("thread", 0x18);
        Gc("gc");
        Cmp(trace + 0x28, 2);
        Ne("identity");
        Load(trace + 8);
        Eq(C(0x48), "identity");
        Load(0x02066BF8);
        Eq(C(0x48), "identity");
        Identities("identity");
        Pair("identity", 0x20, 0x28, 0x38);
        Load(C(0x58));
        foreach (var f in new[]
        {
            (0x34, 0x60u),
            (0x28, 0x64u),
            (0xC8, 0x80u),
            (0x54, 0x84u),
            (0x4C, 0xB0u),
            (0x2C, 0xA0u),
            (0x30, 0xA4u),
            (4, 0xA8u)
        }

        )
        {
            a.Hex("8B90");
            a.Word((uint)f.Item1);
            a.Hex("3B15");
            a.Word(C(f.Item2));
            Ne("metadata");
        }

        Load(C(0x74));
        a.Hex("8B00");
        a.Hex("81B820010000");
        a.Word(QueryHook.ActorProcessEvent);
        Ne("metadata");
        Cmp(C(0xAC), 1);
        a.Jcc(0x84, "change_uin");
        Cmp(C(0xAC), 2);
        a.Jcc(0x84, "default_avatar");
        Cmp(C(0xAC), 3);
        a.Jcc(0x84, "recover");
        a.Jmp("sequence");
        a.Label("change_uin");
        Function(0x20002, 12, 12, 0xFFFF, 22);
        Property(0x400080, 12, false);
        Load(C(0xC0));
        a.Hex("83780400");
        Ne("metadata"); // A new PRI must have no Uin.
        Load(C(0x5C));
        a.Hex("8B10");
        a.Hex("3B15");
        a.Word(C(0xE4));
        Ne("metadata");
        a.Hex("8B50043B15");
        a.Word(C(0xF4));
        Ne("metadata");
        a.Hex("8B50083B15");
        a.Word(C(0xF4));
        Ne("metadata");
        Load(C(0xF4));
        a.Hex("83F802");
        a.Jcc(0x82, "metadata");
        a.Hex("83F815");
        a.Jcc(0x87, "metadata");
        a.Hex("89C1");
        a.Hex("8B35");
        a.Word(C(0xE4));
        a.Hex("BF");
        a.Word(C(0xE00));
        a.Hex("F366A7");
        Ne("metadata");
        a.Jmp("invoke");
        a.Label("default_avatar");
        Function(0x20102, 0, 0, 0xFFFF, 1253);
        Cmp(C(0xB0), 0);
        Ne("metadata");
        Load(C(0xE0));
        a.Hex("83780400");
        Ne("metadata");
        Load(C(0xEC));
        a.Hex("833800");
        Ne("metadata");
        Load(C(0xF0));
        a.Hex("833800");
        Ne("metadata");
        a.Jmp("invoke");
        a.Label("recover");
        Function(0x20401, 4, 4, 0, 2);
        Property(0x580, 4, true);
        Load(C(0xE0));
        a.Hex("8B4004");
        Eq(C(0xE8), "metadata");
        Zero("metadata");
        Load(C(0x5C));
        a.Hex("8338FF");
        Ne("metadata");
        a.Label("invoke");
        Set(C(0x10), 0);
        Set(C(4), 1);
        a.Hex("8925");
        a.Word(C(0x68));
        a.Hex("6A00FF35");
        a.Word(C(0x5C));
        a.Hex("FF35");
        a.Word(C(0x58));
        a.Hex("8B0D");
        a.Word(C(0x74));
        a.Hex("8B01FF9020010000");
        a.Hex("8925");
        a.Word(C(0x6C));
        Set(C(0x30), 0);
        Cmp(C(0xAC), 3);
        a.Jcc(0x85, "returned");
        Load(C(0x5C));
        a.Hex("8B00");
        Store(C(0x30));
        a.Label("returned");
        Load(C(0x68));
        Eq(C(0x6C), "changed");
        Thread("changed", 0x1C);
        Gc("changed");
        Load(0x02066BF8);
        Eq(C(0x48), "changed");
        Identities("changed");
        Pair("changed", 0x24, 0x2C, 0x3C);
        Set(C(0x0C), 1);
        Load(C(0xAC));
        Store(C(OrdinalOffset));
        a.Jmp("complete");
        a.Label("changed");
        Set(C(0x0C), 2);
        a.Label("complete");
        Set(C(8), 1);
        a.Jmp("done");
        foreach (var error in new[]
        {
            ("sequence", 1u),
            ("thread", 2u),
            ("gc", 3u),
            ("identity", 4u),
            ("metadata", 5u)
        }

        )
        {
            a.Label(error.Item1);
            Set(C(0x10), error.Item2);
            a.Hex("F0FF05");
            a.Word(C(0x14));
            a.Jmp("done");
        }

        a.Label("done");
        return[..wrapper.AsSpan(0, insertion).ToArray(), ..a.Finish(), ..tail];
    }

    sealed class Assembly
    {
        readonly List<byte> code = [];
        readonly Dictionary<string, int> labels = [];
        readonly List<(int Offset, string Label)> branches = [];
        public void Hex(string hex) => code.AddRange(Convert.FromHexString(hex));
        public void Bytes(byte[] bytes) => code.AddRange(bytes);
        public void Word(uint value)
        {
            byte[] bytes = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            code.AddRange(bytes);
        }

        public void Half(ushort value)
        {
            byte[] bytes = new byte[2];

            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            code.AddRange(bytes);
        }

        public void Label(string value) => labels.Add(value, code.Count);
        public void Jmp(string label)
        {
            code.Add(0xE9);
            Branch(label);
        }

        public void Jcc(byte condition, string label)
        {
            code.Add(0x0F);
            code.Add(condition);
            Branch(label);
        }

        void Branch(string label)
        {
            branches.Add((code.Count, label));
            Word(0);
        }

        public byte[] Finish()
        {
            byte[] result = code.ToArray();

            foreach (var branch in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch.Offset, 4), labels[branch.Label] - branch.Offset - 4);
            return result;
        }
    }
}
