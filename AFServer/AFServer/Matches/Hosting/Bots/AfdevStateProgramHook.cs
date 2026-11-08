using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Installs a fixed native state program. Command-page reuse requires completion confirmation followed by
/// IdleAck.
/// </summary>
static class StateProgramHook
{
    public const int CommandSize = 0x500;
    public const uint IdleAckOffset = 0xF04;

    public static byte[] Append(byte[] prefix, uint traceAddress, uint factoryCommand, uint command)
    {
        byte[] tail = TickHook.Build(0, traceAddress).AsSpan(0xBC).ToArray();
        int insertion = prefix.Length - tail.Length;

        if (insertion < 0 || !prefix.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("State program requires the reviewed post-tick tail.");

        var a = new Assembly();

        uint C(uint offset) => command + offset;
        void Load(uint address) { a.Hex("A1"); a.Word(address); }
        void Store(uint address) { a.Hex("A3"); a.Word(address); }
        void Set(uint address, uint value) { a.Hex("C705"); a.Word(address); a.Word(value); }
        void Compare(uint address, uint value) { a.Hex("813D"); a.Word(address); a.Word(value); }
        void Equal(uint address) { a.Hex("3B05"); a.Word(address); a.Jcc(0x85, "failed"); }
        void Nonzero() { a.Hex("85C0"); a.Jcc(0x84, "failed"); }

        Compare(C(0), 1); a.Jcc(0x84, "requested");
        a.Hex("F0FF05"); a.Word(C(IdleAckOffset)); a.Jmp("done");
        a.Label("requested");
        Compare(C(8), 0); a.Jcc(0x85, "done");
        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
        {
            Compare(address, 0); a.Jcc(0x85, "done");
        }
        a.Hex("F705"); a.Word(0x01FCE998); a.Word(1); a.Jcc(0x85, "failed");
        a.Hex("FF15"); a.Word(0x019F6228); Store(C(0x2C)); Equal(C(0x70));
        Load(0x01FE22C0); Equal(C(0x70));
        Compare(0x01FE22C4, 1); a.Jcc(0x85, "failed");
        Load(0x02063820); Equal(C(0x74));
        a.Hex("83B87806000000"); a.Jcc(0x85, "failed");
        Load(0x02066BF8); Equal(C(0x40));
        a.Hex("8B4050"); Nonzero();
        a.Hex("83784000"); a.Jcc(0x8E, "failed");
        a.Hex("8B403C"); Nonzero();
        a.Hex("8B00"); Equal(C(0x44));
        a.Hex("8B8014040000"); Equal(C(0x78));

        // The factory must have returned and reported the exact same Controller. Unknown completion forbids creation or reinstallation.
        Compare(factoryCommand + 8, 1); a.Jcc(0x85, "failed");
        Compare(factoryCommand + 0x0C, 1); a.Jcc(0x85, "failed");
        Load(factoryCommand + 0x30); Equal(C(0x48));
        // Validate table slots, names, classes, and Outer for every script object/property reference before installation or observation.
        Load(C(0x84)); a.Hex("83F820"); a.Jcc(0x87, "failed"); Nonzero();
        a.Hex("89C7BE"); a.Word(C(0x100));
        a.Label("identities"); a.Hex("8B06"); Nonzero();
        a.Hex("8B560485D2"); a.Jcc(0x88, "failed");
        a.Hex("3B15"); a.Word(0x02053EAC); a.Jcc(0x8D, "failed");
        a.Hex("8B0D"); a.Word(0x02053EA8); a.Hex("85C9"); a.Jcc(0x84, "failed");
        a.Hex("390491"); a.Jcc(0x85, "failed");

        foreach (var f in new[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
        {
            a.Hex("8B50"); a.Hex(f.Item1.ToString("X2"));
            a.Hex("3B56"); a.Hex(f.Item2.ToString("X2")); a.Jcc(0x85, "failed");
        }

        a.Hex("F7400C00000020"); a.Jcc(0x85, "failed");
        a.Hex("83C6184F"); a.Jcc(0x85, "identities");
        Load(C(0x48));
        a.Hex("F780B000000008000000"); a.Jcc(0x85, "failed");
        // The program drives only its first bound live Pawn. After Pawn replacement, stop and create a new behavior task.
        Load(C(0x90)); a.Hex("8B00"); Equal(C(0x8C)); Nonzero();
        a.Hex("F780B000000008000000"); a.Jcc(0x85, "failed");
        Load(C(0x94)); a.Hex("8B00"); Equal(C(0x48));
        Load(C(0x98)); a.Hex("8B00"); Equal(C(0x44));
        Load(C(0x9C)); a.Hex("833800"); a.Jcc(0x8E, "failed");
        Load(C(0x48));
        a.Hex("8B0D"); a.Word(C(0x80)); a.Hex("8B1408");
        a.Hex("3B15"); a.Word(C(0x44)); a.Jcc(0x85, "failed");
        a.Hex("8B4018"); Equal(C(0x5C)); Nonzero();
        a.Hex("8B5010"); a.Hex("3B15"); a.Word(C(0x60)); a.Jcc(0x85, "failed");
        a.Hex("8B5014"); a.Hex("3B15"); a.Word(C(0x48)); a.Jcc(0x85, "failed");
        a.Hex("8B5028"); a.Hex("3B15"); a.Word(C(0x64)); a.Jcc(0x85, "failed");
        Store(C(0x14));

        Compare(C(4), 0); a.Jcc(0x85, "observe");
        a.Hex("83781800"); a.Jcc(0x85, "failed");
        a.Hex("6683783000"); a.Jcc(0x85, "failed");
        Store(C(0x10));
        a.Hex("8B15"); a.Word(C(0x68));
        Set(C(4), 1); // Initial publication cannot retry; retain the page until instance shutdown.
        a.Hex("895018");
        Set(C(0x0C), 1);
        a.Jmp("done");

        a.Label("observe");
        a.Hex("8B5018"); a.Hex("8915"); a.Word(C(0x1C));
        a.Hex("0FB74830"); a.Hex("890D"); a.Word(C(0x24));
        a.Hex("85D2"); a.Jcc(0x84, "stopped");
        a.Hex("3B15"); a.Word(C(0x68)); a.Jcc(0x84, "done"); // Native ProcessState has not consumed it yet; retain a bounded wait.
        a.Hex("3B15"); a.Word(C(0x6C)); a.Jcc(0x85, "failed");
        a.Hex("3B0D"); a.Word(C(0x7C)); a.Jcc(0x85, "failed");
        a.Hex("FF05"); a.Word(C(0x28));
        a.Jmp("done");

        a.Label("stopped");
        a.Hex("85C9"); a.Jcc(0x85, "failed");
        Compare(C(0x88), 0); a.Jcc(0x84, "complete");
        Compare(C(0x28), 0); a.Jcc(0x84, "failed");
        a.Label("complete");
        Set(C(0x0C), 2); Set(C(8), 1); a.Jmp("done");
        a.Label("failed"); Set(C(0x0C), 3); Set(C(8), 1);
        a.Label("done");

        return [.. prefix.AsSpan(0, insertion).ToArray(), .. a.Finish(), .. tail];
    }

    sealed class Assembly
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
        public void Label(string label) => labels.Add(label, code.Count);
        public void Jcc(byte condition, string label) { code.Add(0x0F); code.Add(condition); Branch(label); }
        public void Jmp(string label) { code.Add(0xE9); Branch(label); }
        private void Branch(string label) { branches.Add((code.Count, label)); Word(0); }
        public byte[] Finish()
        {
            byte[] bytes = code.ToArray();

            foreach (var branch in branches)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(branch.Offset, 4), labels[branch.Label] - branch.Offset - 4);

            return bytes;
        }
    }
}
