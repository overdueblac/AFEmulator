using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// This round's preparation coordination page. Installs the exact PendingMatch.StartMatch entry only on the
/// game thread, delaying normal starts until filling completes. Original Timer, world time, and human
/// loading continue; after release, later match/round entries are unrestricted.
/// </summary>
internal static class AfdevBioBotPreparationHook
{
    public const int PageSize = 4096;
    public const uint Request = 0;
    public const uint Status = 4;
    public const uint IdleAck = 8;
    public const uint BlockedStarts = 0x0C;
    public const uint RosterComplete = 0x10;
    public const uint ReleasedStarts = 0x14;
    public const uint Installed = 1;
    public const uint Released = 2;
    public const uint InvalidBinding = 3;
    public const uint BotCount = 0x20;
    public const uint HumanCount = 0x24;
    public const uint ObserverCount = 0x28;
    public const uint FighterCapacity = 0x2C;
    public const uint RegistrationCounter = 0x30;
    public const uint World = 0x34;
    public const uint WorldInfo = 0x38;
    public const uint Game = 0x3C;
    public const uint Engine = 0x40;
    public const uint Thread = 0x44;
    public const uint NetDriver = 0x48;
    public const uint PendingState = 0x4C;
    public const uint StartFunction = 0x50;
    public const uint StartOriginal = 0x54;
    public const uint StartEntry = 0x58;
    public const uint TimerFunction = 0x5C;
    public const uint TimerOriginal = 0x60;
    public const uint TimerEntry = 0x64;
    public const uint NumPlayers = 0x68;
    public const uint NumBots = 0x6C;
    public const uint NumTravelling = 0x70;
    public const uint NumSpectators = 0x74;
    public const uint MaxPlayers = 0x78;
    public const uint IdentityCount = 0x80;
    public const uint ProofCount = 0x84;
    public const uint MinNetPlayers = 0x88;
    public const uint WaitingAddress = 0x8C;
    public const uint WaitingMask = 0x90;
    public const uint WarmupAddress = 0x94;
    public const uint WarmupMask = 0x98;
    public const uint NetModeAddress = 0x9C;
    public const uint Self = 0xA0;
    public const uint ActivationCommands = 0x100;
    public const uint Identities = 0x200;
    public const uint Proofs = 0x600;
    public const int IdentityCapacity = 32;
    public const int ProofCapacity = 128;

    /// <summary>
    /// Appends before World.Tick. Installation is one-time; retain all code and descriptor pages until the
    /// original Job ends.
    /// </summary>
    public static byte[] Append(byte[] wrapper, uint command)
    {
        byte[] anchor = Convert.FromHexString("0FAE0C2489DC619DFF742408FF742408E8");
        int insertion = wrapper.AsSpan().IndexOf(anchor);

        if (command < 0x10000 || (command & 3) != 0 || insertion < 0 ||
            wrapper.AsSpan(insertion + 1).IndexOf(anchor) >= 0)
            throw new InvalidDataException("BIO 准备协调门缺少唯一的世界 Tick 保存边界。");

        byte[] body = BuildInstall(command);
        byte[] result = [.. wrapper.AsSpan(0, insertion), .. body, .. wrapper.AsSpan(insertion)];
        int displacement = insertion + body.Length + anchor.Length;
        int original = BinaryPrimitives.ReadInt32LittleEndian(wrapper.AsSpan(insertion + anchor.Length, 4));

        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(displacement, 4), checked(original - body.Length));
        return result;
    }

    private static byte[] BuildInstall(uint command)
    {
        var a = new Assembly();

        uint C(uint offset) => command + offset;
        void Load(uint address) { a.Hex("A1"); a.Word(address); }
        void Compare(uint address, uint value) { a.Hex("813D"); a.Word(address); a.Word(value); }
        void Equal(uint address) { a.Hex("3B05"); a.Word(address); a.Jcc(0x85, "invalid"); }
        void Set(uint offset, uint value) { a.Hex("C705"); a.Word(C(offset)); a.Word(value); }

        a.Hex("60");
        Compare(C(Request), 1); a.Jcc(0x85, "idle");
        Compare(C(Status), 0); a.Jcc(0x85, "done");
        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
        {
            Compare(address, 0); a.Jcc(0x85, "done");
        }

        a.Hex("FF15"); a.Word(0x019F6228); Equal(C(Thread));
        Load(0x01FE22C0); Equal(C(Thread));
        Compare(0x01FE22C4, 1); a.Jcc(0x85, "invalid");
        Load(0x02063820); Equal(C(Engine));
        a.Hex("83B87806000000"); a.Jcc(0x85, "invalid");
        Load(0x02066BF8); Equal(C(World));
        Load(C(Game)); a.Hex("8B4018"); a.Pointer("invalid");
        a.Hex("8B50143B15"); a.Word(C(Game)); a.Jcc(0x85, "invalid");
        a.Hex("8B4028"); Equal(C(PendingState));
        Load(C(World)); a.Hex("8B80D8000000"); Equal(C(NetDriver));
        a.Hex("83784800"); a.Jcc(0x85, "invalid");

        // Only the initial installation before endpoint publication still requires no humans. Resources, control, and registration use the preparation boundary below.
        foreach (uint slot in new uint[] { NumPlayers, NumBots, NumTravelling, NumSpectators })
        {
            Load(C(slot)); a.Hex("833800"); a.Jcc(0x85, "invalid");
        }

        Load(C(IdentityCount)); a.Hex("83F801"); a.Jcc(0x82, "invalid");
        a.Hex("83F820"); a.Jcc(0x87, "invalid");
        a.Hex("89C7BE"); a.Word(C(Identities));
        a.IdentityLoop("install_ids", "invalid");
        Load(C(ProofCount)); a.Hex("83F801"); a.Jcc(0x82, "invalid");
        a.Hex("3D"); a.Word(ProofCapacity); a.Jcc(0x87, "invalid");
        a.Hex("89C7BE"); a.Word(C(Proofs));
        a.Label("proofs");
        a.Hex("8B068B003B4604"); a.Jcc(0x85, "invalid");
        a.Hex("83C6084F"); a.Jcc(0x85, "proofs");
        Load(C(MaxPlayers)); a.Hex("8B00"); Equal(C(FighterCapacity));
        Load(C(StartFunction)); a.Hex("8B80C8000000"); Equal(C(StartOriginal));
        Load(C(TimerFunction)); a.Hex("8B80C8000000"); Equal(C(TimerOriginal));
        Compare(C(StartOriginal), AfdevBioBotTimerHook.OriginalExec); a.Jcc(0x85, "invalid");
        Compare(C(TimerOriginal), AfdevBioBotTimerHook.OriginalExec); a.Jcc(0x85, "invalid");
        Load(C(StartEntry)); a.Pointer("invalid");
        Load(C(TimerEntry)); a.Pointer("invalid");

        Load(C(StartEntry)); a.Hex("8B15"); a.Word(C(StartFunction)); a.Hex("8982C8000000");
        Load(C(TimerEntry)); a.Hex("8B15"); a.Word(C(TimerFunction)); a.Hex("8982C8000000");
        Set(Status, Installed); a.Jmp("done");
        a.Label("invalid"); Set(Status, InvalidBinding); a.Jmp("done");
        a.Label("idle"); a.Hex("F0FF05"); a.Word(C(IdleAck));
        a.Label("done"); a.Hex("61");
        return a.Finish();
    }

    /// <summary>
    /// Builds the shared preparation boundary from three field addresses in the call descriptor. Returns
    /// EAX=1/0 and preserves other integer registers. Counts derive from frozen capacity and registered
    /// bots, allowing normal human connection/loading interleaving; never infer human slots from bot
    /// counts.
    /// </summary>
    public static byte[] BuildGuard(uint preparationSlot, uint expectedWorldSlot, uint expectedGameSlot)
    {
        var a = new Assembly();

        void Load(uint offset) { a.Hex("8B87"); a.Word(offset); }
        void Equal(uint offset) { a.Hex("3B87"); a.Word(offset); a.Jcc(0x85, "fail"); }
        void Compare(uint offset, uint value) { a.Hex("81BF"); a.Word(offset); a.Word(value); a.Jcc(0x85, "fail"); }

        a.Hex("60A1"); a.Word(preparationSlot); a.Pointer("fail"); a.Hex("89C7");
        Compare(Request, 1); Compare(Status, Installed);
        Load(Thread); a.Hex("3B05"); a.Word(0x01FE22C0); a.Jcc(0x85, "fail");
        a.Hex("FF15"); a.Word(0x019F6228); Equal(Thread);
        a.Hex("833D"); a.Word(0x01FE22C4); a.Hex("01"); a.Jcc(0x85, "fail");
        Load(Engine); a.Hex("3B05"); a.Word(0x02063820); a.Jcc(0x85, "fail");
        a.Hex("83B87806000000"); a.Jcc(0x85, "fail");
        Load(Self); a.Hex("39F8"); a.Jcc(0x85, "fail");
        Load(World); a.Hex("3B05"); a.Word(expectedWorldSlot); a.Jcc(0x85, "fail");
        a.Hex("3B05"); a.Word(0x02066BF8); a.Jcc(0x85, "fail");
        Load(Game); a.Hex("3B05"); a.Word(expectedGameSlot); a.Jcc(0x85, "fail");
        a.Hex("8B4018"); a.Pointer("fail");
        a.Hex("8B50143B97"); a.Word(Game); a.Jcc(0x85, "fail");
        a.Hex("8B4028"); Equal(PendingState);
        Load(World); a.Hex("8B80D8000000"); Equal(NetDriver);
        Load(StartFunction); a.Hex("8B80C8000000"); Equal(StartEntry);
        Load(TimerFunction); a.Hex("8B80C8000000"); Equal(TimerEntry);
        Load(MinNetPlayers); a.Hex("833801"); a.Jcc(0x8C, "fail");
        Load(WaitingAddress); a.Hex("8B002387"); a.Word(WaitingMask); Equal(WaitingMask);
        Load(WarmupAddress); a.Hex("8B002387"); a.Word(WarmupMask); a.Jcc(0x85, "fail");
        Load(NetModeAddress); a.Hex("803801"); a.Jcc(0x85, "fail");
        Load(BotCount); a.Hex("83F801"); a.Jcc(0x82, "fail");
        a.Hex("83F80F"); a.Jcc(0x87, "fail");
        Load(HumanCount); a.Hex("83F801"); a.Jcc(0x82, "fail");
        a.Hex("83F810"); a.Jcc(0x87, "fail");
        a.Hex("0387"); a.Word(BotCount); Equal(FighterCapacity);
        a.Hex("83F810"); a.Jcc(0x87, "fail");
        Load(MaxPlayers); a.Hex("8B00"); Equal(FighterCapacity);
        Load(RegistrationCounter); a.Pointer("fail"); a.Hex("8B18");
        // EBX is a registered count, not a pointer. Read counts only from here onward without native calls.
        a.Hex("89D8");
        a.Hex("3B87"); a.Word(BotCount); a.Jcc(0x87, "fail");
        Load(NumPlayers); a.Hex("8B0029D8"); a.Jcc(0x82, "fail");
        a.Hex("3B87"); a.Word(HumanCount); a.Jcc(0x87, "fail");
        Load(NumBots); a.Hex("833800"); a.Jcc(0x85, "fail");
        Load(NumTravelling); a.Hex("8B003B87"); a.Word(HumanCount); a.Jcc(0x87, "fail");
        Load(NumSpectators); a.Hex("8B003B87"); a.Word(ObserverCount); a.Jcc(0x87, "fail");
        Load(NetDriver); a.Pointer("fail");
        a.Hex("8B48488B504C39D1"); a.Jcc(0x87, "fail");
        a.Hex("83FA40"); a.Jcc(0x87, "fail");
        Load(HumanCount); a.Hex("0387"); a.Word(ObserverCount);
        a.Hex("39C1"); a.Jcc(0x87, "fail");
        a.Hex("C744241C01000000"); a.Jmp("done");
        a.Label("fail"); a.Hex("C744241C00000000");
        a.Label("done"); a.Hex("61");
        return a.Finish();
    }

    /// <summary>
    /// Intercepts only this round's exact state entry and releases the original method once the full roster
    /// and current-human proofs hold.
    /// </summary>
    public static byte[] BuildStart(uint command, IReadOnlyList<uint> activationCommands)
    {
        if (command < 0x10000 || (command & 3) != 0 || activationCommands.Count is 0 or > 15 ||
            activationCommands.Distinct().Count() != activationCommands.Count ||
            activationCommands.Any(value => value < 0x10000 || (value & 3) != 0))
            throw new ArgumentException("原生准备门要求独立页面与有界的 Bot 激活槽。");

        var a = new Assembly();

        uint C(uint offset) => command + offset;
        void Compare(uint address, uint value) { a.Hex("813D"); a.Word(address); a.Word(value); }

        a.Hex("9C6089E381EC2002000083E4F00FAE0424DBE3D92C24FC");
        a.Hex("8B43183B05"); a.Word(C(Game)); a.Jcc(0x85, "original");
        a.Hex("8B4328"); a.Pointer("blocked");
        a.Hex("8B50103B15"); a.Word(C(StartFunction)); a.Jcc(0x85, "original");
        a.Hex("8B50143B15"); a.Word(C(Game)); a.Jcc(0x85, "original");
        Compare(C(Status), Released); a.Jcc(0x84, "original");
        Compare(C(Request), 1); a.Jcc(0x85, "blocked");
        Compare(C(Status), Installed); a.Jcc(0x85, "blocked");
        Compare(C(RosterComplete), 1); a.Jcc(0x85, "blocked");
        a.Bytes(BuildGuard(C(Self), C(World), C(Game)));
        a.Hex("85C0"); a.Jcc(0x84, "invalid");

        // Even non-Timer native start requests rescan current humans and revalidate every registered bot.
        foreach (uint activation in activationCommands)
            a.Bytes(AfdevBioBotActivationHook.BuildBody(activation));

        a.Hex("8B0D"); a.Word(C(BotCount));
        a.Hex("BE"); a.Word(C(ActivationCommands));
        a.Label("participants");
        a.Hex("8B06"); a.Pointer("invalid");
        a.Hex("833801"); a.Jcc(0x85, "blocked");
        a.Hex("83780401"); a.Jcc(0x85, "blocked");
        a.Hex("83783000"); a.Jcc(0x84, "blocked");
        a.Hex("83C60449"); a.Jcc(0x85, "participants");
        a.Hex("A1"); a.Word(C(RegistrationCounter)); a.Hex("8B003B05"); a.Word(C(BotCount));
        a.Jcc(0x85, "blocked");
        a.Hex("C705"); a.Word(C(Status)); a.Word(Released);
        a.Hex("F0FF05"); a.Word(C(ReleasedStarts));
        a.Jmp("original");
        a.Label("invalid");
        a.Hex("C705"); a.Word(C(Status)); a.Word(InvalidBinding);
        a.Label("blocked");
        a.Hex("F0FF05"); a.Word(C(BlockedStarts));
        a.Hex("0FAE0C2489DC619DC20800");
        a.Label("original");
        a.Hex("0FAE0C2489DC619D68"); a.Word(AfdevBioBotTimerHook.OriginalExec); a.Hex("C3");
        return a.Finish();
    }

    private sealed class Assembly
    {
        private readonly List<byte> bytes = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Offset, string Label)> branches = [];

        public void Hex(string value) => bytes.AddRange(Convert.FromHexString(value));
        public void Bytes(byte[] value) => bytes.AddRange(value);
        public void Word(uint value)
        {
            byte[] word = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(word, value);
            bytes.AddRange(word);
        }

        public void Label(string value) => labels.Add(value, bytes.Count);
        public void Jcc(byte opcode, string label) { Hex("0F" + opcode.ToString("X2")); Branch(label); }
        public void Jmp(string label) { Hex("E9"); Branch(label); }
        private void Branch(string label) { branches.Add((bytes.Count, label)); Word(0); }
        public void Pointer(string fail)
        {
            Hex("3D00000100"); Jcc(0x82, fail);
            Hex("3D0000F0FF"); Jcc(0x83, fail);
            Hex("A903000000"); Jcc(0x85, fail);
        }

        public void IdentityLoop(string loop, string fail)
        {
            Label(loop); Hex("8B06"); Pointer(fail);
            Hex("8B56043B15"); Word(0x02053EAC); Jcc(0x83, fail);
            Hex("8B0D"); Word(0x02053EA8); Hex("85C9"); Jcc(0x84, fail);
            Hex("390491"); Jcc(0x85, fail);
            foreach (var field in new[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
            {
                Hex("8B50" + field.Item1.ToString("X2") + "3B56" + field.Item2.ToString("X2"));
                Jcc(0x85, fail);
            }

            Hex("F7400C00000020"); Jcc(0x85, fail);
            Hex("83C6184F"); Jcc(0x85, loop);
        }

        public byte[] Finish()
        {
            byte[] result = bytes.ToArray();

            foreach ((int offset, string label) in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), labels[label] - offset - 4);

            return result;
        }
    }
}
