using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Independent bot-registration slots before current-world Tick. Each frame scans frozen humans from the
/// current NetDriver without relying on relay diagnostics. Registers only NumPlayers; native BIO PC Logout
/// uses that same count, so never increment NumBots too.
/// </summary>
internal static class AfdevBioBotActivationHook
{
    public const int PageSize = 16384;
    public const uint IdleAck = 0x0C;
    public const uint Status = 0x04;
    public const uint Registered = 1;
    public const uint WorldChanged = 2;
    public const uint MatchAlreadyStarted = 3;
    public const uint InvalidIdentity = 4;
    public const uint InvalidCounts = 5;
    public const int IdentityOffset = 0x800;
    public const int IdentityCapacity = 32;
    // Team/Size/TeamIndex add 16 proof entries, bringing the full layout to 172. Proof occupies a separate page-end region
    // [0x3000,0x3800), leaving identity [0x800,0xB00), bot strings, and the human table of up to 32 rows intact.
    public const int ProofOffset = 0x3000;
    public const int ProofCapacity = 256;
    public const uint BotCount = 0x188;
    public const uint HumanCount = 0x18C;
    public const uint FighterCapacity = 0x190;
    public const uint ObserverCount = 0x194;
    public const uint RegistrationCounter = 0x198;
    public const uint BotIndex = 0x19C;
    public const uint RegisteredCount = 0x1A0;
    public const uint TeamSize = 0x1A4;
    public const uint PriTeam = 0x1A8;
    public const uint Team = 0x1AC;
    public const uint PreparationPage = 0x1B0;
    public const uint HumanInitialProcessing = 0x1B4;
    public const uint HumanLoadingComplete = 0x1BC;
    public const uint HumanWaitingLoading = 0x1C4;
    public const int HumanTableOffset = 0x1000;
    public const int HumanStride = 0x100;
    public const int HumanMatchOffset = 0xE4;
    public const int HumanConnectionOffset = 0xE8;
    public const int HumanControllerOffset = 0xEC;
    public const int HumanPriOffset = 0xF0;

    /// <summary>
    /// Adds a static gate during suspended installation. Write the full descriptor first, confirm Timer
    /// installation through Request=2, then publish scanning. Terminal state cannot be republished.
    /// </summary>
    public static byte[] Append(byte[] wrapper, uint command)
    {
        byte[] anchor = Convert.FromHexString("0FAE0C2489DC619DFF742408FF742408E8");
        int insertion = wrapper.AsSpan().IndexOf(anchor);

        if (insertion < 0 || wrapper.AsSpan(insertion + 1).IndexOf(anchor) >= 0)
            throw new InvalidDataException("激活门找不到唯一的原生 Tick 前保存边界。");

        byte[] gate = BuildBody(command);
        byte[] result = [.. wrapper.AsSpan(0, insertion), .. gate, .. wrapper.AsSpan(insertion)];
        int displacement = insertion + gate.Length + anchor.Length;
        int originalDisplacement = BinaryPrimitives.ReadInt32LittleEndian(wrapper.AsSpan(insertion + anchor.Length, 4));

        // The insertion precedes the original callee rel32, requiring correction of the original World.Tick call displacement.
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(displacement, 4), checked(originalDisplacement - gate.Length));
        return result;
    }

    /// <summary>
    /// Runs after the caller saves registers and floating-point state. Tick and Timer share the same
    /// descriptor page and nonrepeatable state.
    /// </summary>
    internal static byte[] BuildBody(uint command)
    {
        var a = new Assembly();

        uint C(uint offset) => command + offset;
        void Load(uint address) { a.Hex("A1"); a.Word(address); }
        void Store(uint address) { a.Hex("A3"); a.Word(address); }
        void Set(uint address, uint value) { a.Hex("C705"); a.Word(address); a.Word(value); }
        void Cmp(uint address, uint value) { a.Hex("813D"); a.Word(address); a.Word(value); }
        void Eq(uint address, string fail) { a.Hex("3B05"); a.Word(address); a.Jcc(0x85, fail); }
        void Nonzero(string fail) { a.Hex("85C0"); a.Jcc(0x84, fail); }
        void Pointer(string fail)
        {
            a.Hex("3D00000100"); a.Jcc(0x82, fail);
            a.Hex("3D0000F0FF"); a.Jcc(0x83, fail);
            a.Hex("A903000000"); a.Jcc(0x85, fail);
        }

        void Object(string fail)
        {
            Pointer(fail);
            a.Hex("8B48043B0D"); a.Word(0x02053EAC); a.Jcc(0x83, fail);
            a.Hex("8B15"); a.Word(0x02053EA8);
            a.Hex("85D2"); a.Jcc(0x84, fail);
            a.Hex("39048A"); a.Jcc(0x85, fail);
            a.Hex("F7400C00000020"); a.Jcc(0x85, fail);
            a.Hex("F780B000000008000000"); a.Jcc(0x85, fail);
        }

        void Offset(uint offset)
        {
            a.Hex("8B0D"); a.Word(C(offset));
            a.Hex("8B0408");
        }

        void Text(uint objectSlot, uint offsetSlot, uint lengthSlot, uint expected, string fail, string name)
        {
            Load(C(objectSlot));
            a.Hex("0305"); a.Word(C(offsetSlot));
            a.Hex("8B48043B0D"); a.Word(C(lengthSlot)); a.Jcc(0x85, fail);
            a.Hex("3B4808"); a.Jcc(0x87, fail);
            a.Hex("81780800000100"); a.Jcc(0x83, fail);
            a.Hex("8B00"); Pointer(fail);
            a.Hex("BA"); a.Word(C(expected));
            a.Label(name);
            a.Hex("668B18663B1A"); a.Jcc(0x85, fail);
            a.Hex("83C00283C20249"); a.Jcc(0x85, name);
        }

        void HumanText(uint fieldOffset, uint lengthOffset, uint textOffset, string name)
        {
            Load(C(0x1C));
            a.Hex("0305"); a.Word(C(fieldOffset));
            a.Hex("8B48048B15"); a.Word(C(0x34));
            a.Hex("3B4A" + lengthOffset.ToString("X2")); a.Jcc(0x85, "next_human");
            a.Hex("3B4808"); a.Jcc(0x87, "next_human");
            a.Hex("81780800000100"); a.Jcc(0x83, "next_human");
            a.Hex("8B00"); Pointer("next_human");
            a.Hex("81C2"); a.Word(textOffset);
            a.Label(name);
            a.Hex("668B18663B1A"); a.Jcc(0x85, "next_human");
            a.Hex("83C00283C20249"); a.Jcc(0x85, name);
        }

        // EBX retains the wrapper's floating-point context stack; restore it unchanged after temporary gate use.
        a.Hex("53");
        Cmp(C(0), 1); a.Jcc(0x84, "requested");
        Cmp(C(0), 2); a.Jcc(0x85, "idle");
        a.Label("requested");
        Cmp(C(Status), 0); a.Jcc(0x84, "validate");
        Cmp(C(Status), Registered); a.Jcc(0x85, "done");
        // Registration commits once. Refresh current-human proofs while the preparation gate is closed so old connections cannot authorize later starts.
        Load(C(PreparationPage)); a.Hex("85C0"); a.Jcc(0x84, "done");
        Pointer("identity");
        a.Hex("83780401"); a.Jcc(0x85, "done");
        a.Hex("833801"); a.Jcc(0x85, "done");
        a.Label("validate");
        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
        {
            Cmp(address, 0); a.Jcc(0x85, "done");
        }

        Load(0x01FE22C0); Eq(C(0xAC), "world"); Nonzero("world");
        a.Hex("FF15"); a.Word(0x019F6228); Eq(C(0xAC), "world");
        Cmp(0x01FE22C4, 1); a.Jcc(0x85, "world");
        Load(0x02063820); Pointer("world"); Eq(C(0xB8), "world");
        a.Hex("83B87806000000"); a.Jcc(0x85, "world");
        Load(0x02066BF8); Eq(C(0x40), "world");
        a.Hex("8B4050"); Pointer("world");
        a.Hex("83784000"); a.Jcc(0x8E, "world");
        a.Hex("8B403C"); Pointer("world");
        a.Hex("8B00"); Eq(C(0x44), "world");
        a.Hex("8B8014040000"); Eq(C(0x48), "world");

        Load(C(0xB0)); a.Hex("3D"); a.Word(IdentityCapacity); a.Jcc(0x87, "identity"); Nonzero("identity");
        a.Hex("89C7BE"); a.Word(C(IdentityOffset));
        a.Label("ids");
        a.Hex("8B06"); Pointer("identity");
        a.Hex("8B56043B15"); a.Word(0x02053EAC); a.Jcc(0x83, "identity");
        a.Hex("8B0D"); a.Word(0x02053EA8);
        a.Hex("85C9"); a.Jcc(0x84, "identity");
        a.Hex("390491"); a.Jcc(0x85, "identity");
        foreach (var field in new[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
        {
            a.Hex("8B50" + field.Item1.ToString("X2") + "3B56" + field.Item2.ToString("X2"));
            a.Jcc(0x85, "identity");
        }

        a.Hex("F7400C00000020"); a.Jcc(0x85, "identity");
        a.Hex("83C6184F"); a.Jcc(0x85, "ids");
        Load(C(0xB4)); a.Hex("3D"); a.Word(ProofCapacity); a.Jcc(0x87, "identity"); Nonzero("identity");
        a.Hex("89C7BE"); a.Word(C(ProofOffset));
        a.Label("proofs");
        a.Hex("8B068B003B4604"); a.Jcc(0x85, "identity");
        a.Hex("83C6084F"); a.Jcc(0x85, "proofs");

        Load(C(0x48)); a.Hex("8B4018"); Pointer("world");
        a.Hex("8B4028"); Eq(C(0x4C), "started");
        Load(C(0x50)); Object("identity");
        Load(C(0x50)); Offset(0x60); a.Hex("85C0"); a.Jcc(0x85, "identity");
        Load(C(0x50)); Offset(0x184); a.Hex("85C0"); a.Jcc(0x85, "identity");
        Load(C(0x50)); Offset(0x64); Eq(C(0x54), "identity");
        Load(C(0x50)); Offset(0x68); Eq(C(0x44), "identity");
        Load(C(0x54)); Object("identity");
        Load(C(0x54)); Offset(0x6C); Eq(C(0x50), "identity");
        Load(C(0x54)); Offset(0x70); Eq(C(0x44), "identity");
        // SetWaitingPlayer(true) sets both IsSpectator and WaitingPlayer. Exclude actual observers separately through OnlySpectator
        // and TG PRI.IsGamePlayer's three bits; never clear temporary waiting flags to satisfy the activation gate.
        foreach (uint bit in new uint[] { 0x7C, 0x144, 0x14C, 0x154 })
        {
            Load(C(0x54)); Offset(bit);
            a.Hex("2305"); a.Word(C(bit + 4)); a.Jcc(0x85, "identity");
        }

        foreach (uint bit in new uint[] { 0x84, 0x15C })
        {
            Load(C(0x54)); Offset(bit);
            a.Hex("2305"); a.Word(C(bit + 4)); Eq(C(bit + 4), "identity");
        }

        foreach (uint slot in new uint[] { 0xC0, 0xCC, 0xD8, 0xE4, 0xF0, 0xFC, 0x108, 0x164 })
        {
            Load(C(slot)); a.Hex("8B002305"); a.Word(C(slot + 4)); Eq(C(slot + 8), "identity");
        }

        Load(C(0x50)); a.Hex("8B4018"); Pointer("identity");
        a.Hex("8B50143B15"); a.Word(C(0x50)); a.Jcc(0x85, "identity");
        a.Hex("8B4028"); Eq(C(0x120), "identity");
        Load(C(0x170)); a.Hex("833801"); a.Jcc(0x8C, "counts");
        Load(C(0x40)); a.Hex("8B80D8000000"); Pointer("world"); Eq(C(0x174), "world"); Store(C(0x10));

        // The native loaded predicate's sole standby-connection exception must still exclude this bot without a Player.
        Load(0x02073234); a.Hex("85C0"); a.Jcc(0x84, "backup_clear");
        Pointer("identity"); a.Hex("8B40403B05"); a.Word(C(0x50)); a.Jcc(0x84, "identity");
        a.Label("backup_clear");
        Text(0x54, 0x74, 0xA4, 0xE00, "identity", "bot_uin");
        Text(0x54, 0x78, 0xA8, 0xE80, "identity", "bot_name");

        Load(C(BotCount)); a.Hex("83F801"); a.Jcc(0x82, "counts");
        a.Hex("83F80F"); a.Jcc(0x87, "counts");
        Load(C(HumanCount)); a.Hex("83F801"); a.Jcc(0x82, "counts");
        a.Hex("83F810"); a.Jcc(0x87, "counts");
        a.Hex("0305"); a.Word(C(BotCount)); Eq(C(FighterCapacity), "counts");
        a.Hex("83F810"); a.Jcc(0x87, "counts");
        Load(C(0x98)); a.Hex("8B00"); Eq(C(FighterCapacity), "counts");
        Load(C(PriTeam)); a.Hex("8B00"); Eq(C(Team), "identity");
        Load(C(TeamSize)); a.Hex("8B003B05"); a.Word(C(BotCount)); a.Jcc(0x82, "counts");
        a.Hex("3B05"); a.Word(C(FighterCapacity)); a.Jcc(0x87, "counts");

        Cmp(C(PreparationPage), 0); a.Jcc(0x84, "preparation_checked");
        a.Bytes(AfdevBioBotPreparationHook.BuildGuard(C(PreparationPage), C(0x40), C(0x48)));
        a.Hex("85C0"); a.Jcc(0x84, "counts");
        Load(C(PreparationPage));
        a.Hex("8B50203B15"); a.Word(C(BotCount)); a.Jcc(0x85, "identity");
        a.Hex("8B50303B15"); a.Word(C(RegistrationCounter)); a.Jcc(0x85, "identity");
        a.Label("preparation_checked");
        Cmp(C(0), 2); a.Jcc(0x85, "scan");
        // Production parallel preparation uses the installed round entry point. Retain the no-human installation constraint for independent diagnostics.
        Cmp(C(PreparationPage), 0); a.Jcc(0x85, "install_timer");
        foreach (uint slot in new uint[] { 0x8C, 0x90, 0x94, 0x180 })
        {
            Load(C(slot)); a.Hex("833800"); a.Jcc(0x85, "counts");
        }

        Load(C(0x10)); a.Hex("83784800"); a.Jcc(0x85, "counts");
        Load(C(TeamSize)); a.Hex("8B00"); Eq(C(BotCount), "counts");
        a.Label("install_timer");
        Cmp(C(0x13C), 0); a.Jcc(0x85, "identity");
        Cmp(C(0x134), AfdevBioBotTimerHook.OriginalExec); a.Jcc(0x85, "identity");
        Load(C(0x130)); Pointer("identity");
        a.Hex("8B80C80000003B05"); a.Word(C(0x138)); a.Jcc(0x84, "timer_installed");
        Eq(C(0x134), "identity");
        Load(C(0x138)); Pointer("identity");
        a.Hex("8B15"); a.Word(C(0x130));
        a.Hex("8982C8000000");
        a.Label("timer_installed");
        Set(C(0x13C), 1);
        Set(C(0), 0);
        a.Jmp("done");
        a.Label("scan");
        Cmp(C(0x13C), 1); a.Jcc(0x85, "identity");
        Load(C(0x130)); a.Hex("8B80C8000000"); Eq(C(0x138), "identity");

        // A prior slot's completion bit cannot replace this slot's identity; shared counts constrain only nonrepeatable serialized commit order.
        Load(C(RegistrationCounter)); a.Hex("85C0"); a.Jcc(0x85, "registration_pointer");
        a.Hex("B8"); a.Word(C(RegisteredCount));
        a.Label("registration_pointer"); Pointer("identity"); Store(C(0x3C));
        a.Hex("8B00");
        Cmp(C(Status), Registered); a.Jcc(0x84, "registered_order");
        a.Hex("3B05"); a.Word(C(BotIndex)); a.Jcc(0x85, "done");
        a.Hex("3B05"); a.Word(C(BotCount)); a.Jcc(0x83, "counts");
        a.Jmp("order_checked");
        a.Label("registered_order");
        a.Hex("3B05"); a.Word(C(BotIndex)); a.Jcc(0x86, "counts");
        a.Hex("3B05"); a.Word(C(BotCount)); a.Jcc(0x87, "counts");
        a.Label("order_checked");

        Load(C(HumanCount)); a.Hex("89C1BA"); a.Word(C(HumanTableOffset + HumanMatchOffset));
        a.Label("clear_human_matches");
        a.Hex("C7020000000081C2"); a.Word(HumanStride);
        a.Hex("49"); a.Jcc(0x85, "clear_human_matches");

        Set(C(0x30), 0);
        Load(C(0x10));
        a.Hex("8B68488B484C39CD"); a.Jcc(0x87, "identity");
        a.Hex("83F940"); a.Jcc(0x87, "identity");
        a.Hex("85ED"); a.Jcc(0x84, "done");
        a.Hex("8B4044"); Pointer("identity"); a.Hex("89C6");
        a.Label("connections");
        a.Hex("8B06"); Pointer("identity"); a.Hex("89C7");
        // Connection is an engine network object. Never decode its +4 or class fields as UObject data.
        a.Hex("8B4060"); Eq(C(0x10), "identity");
        a.Hex("8B4740"); Nonzero("next"); Object("identity"); Store(C(0x18));
        a.Hex("8B4034"); Eq(C(0x58), "next");
        Load(C(0x18)); Offset(0x60); a.Hex("39F8"); a.Jcc(0x85, "identity");
        Load(C(0x18)); Offset(0x68); Eq(C(0x44), "next");
        Load(C(0x18)); Offset(0x64); Nonzero("next"); Object("identity"); Store(C(0x1C));
        a.Hex("8B4034"); Eq(C(0x5C), "next");
        Load(C(0x1C)); Offset(0x6C); Eq(C(0x18), "identity");
        Load(C(0x1C)); Offset(0x70); Eq(C(0x44), "next");
        foreach (uint bit in new uint[] { 0x7C, 0x144, 0x14C, 0x154, 0x178 })
        {
            Load(C(0x1C)); Offset(bit);
            a.Hex("2305"); a.Word(C(bit + 4)); a.Jcc(0x85, "next");
        }

        // Humans awaiting delayed startup are also in exact PlayerWaiting; WaitDelay may keep Ready false within the same frame.
        // Check paired waiting semantics and native counts while preserving each human's own loading/readiness sequence.
        foreach (uint bit in new uint[] { 0x84, 0x15C })
        {
            Load(C(0x1C)); Offset(bit);
            a.Hex("2305"); a.Word(C(bit + 4)); Eq(C(bit + 4), "next");
        }

        Cmp(C(PreparationPage), 0); a.Jcc(0x84, "human_loading_checked");
        // ServerSetProcessingComplete means the human character profile is complete, sufficient for PendingMatch preparation checks.
        // bWaitingLoadingProcessComplete is cleared only after post-start RestartPlayer initiates an RPC handshake;
        // waiting for it at the start gate deadlocks StartMatch against the spawn handshake. Keep the bot's full loading gate unchanged.
        foreach (var bit in new[] { (0x18u, HumanInitialProcessing), (0x1Cu, HumanLoadingComplete) })
        {
            Load(C(bit.Item1)); Offset(bit.Item2);
            a.Hex("2305"); a.Word(C(bit.Item2 + 4)); Eq(C(bit.Item2 + 4), "next");
        }

        a.Label("human_loading_checked");
        Load(C(0x18)); a.Hex("8B4018"); Pointer("identity");
        a.Hex("8B50143B15"); a.Word(C(0x18)); a.Jcc(0x85, "identity");
        a.Hex("8B4028"); Eq(C(0x120), "next");
        Set(C(0x34), C(HumanTableOffset));
        Load(C(HumanCount)); Store(C(0x38));
        a.Label("human_candidate");
        HumanText(0x74, 0x10, 0x20, "human_uin");
        HumanText(0x78, 0x14, 0x60, "human_name");
        Load(C(0x34));
        a.Hex("83B8"); a.Word(HumanMatchOffset); a.Hex("00"); a.Jcc(0x85, "identity");
        a.Hex("C780"); a.Word(HumanMatchOffset); a.Word(1);
        a.Hex("89B8"); a.Word(HumanConnectionOffset);
        a.Hex("8B15"); a.Word(C(0x18)); a.Hex("8990"); a.Word(HumanControllerOffset);
        a.Hex("8B15"); a.Word(C(0x1C)); a.Hex("8990"); a.Word(HumanPriOffset);
        a.Hex("FF05"); a.Word(C(0x30));
        a.Hex("893D"); a.Word(C(0x20));
        Load(C(0x18)); Store(C(0x24));
        Load(C(0x1C)); Store(C(0x28));
        a.Jmp("next");
        a.Label("next_human");
        a.Hex("8105"); a.Word(C(0x34)); a.Word(HumanStride);
        a.Hex("FF0D"); a.Word(C(0x38)); a.Jcc(0x85, "human_candidate");
        a.Label("next");
        a.Hex("83C6044D"); a.Jcc(0x85, "connections");
        Cmp(C(0x30), 0); a.Jcc(0x84, "done");
        Load(C(0x30)); a.Hex("3B05"); a.Word(C(HumanCount)); a.Jcc(0x87, "identity");
        Load(C(0x90)); a.Hex("833800"); a.Jcc(0x85, "counts");
        Load(C(0x94)); a.Hex("8B003B05"); a.Word(C(HumanCount)); a.Jcc(0x87, "counts");
        Load(C(0x180)); a.Hex("8B003B05"); a.Word(C(ObserverCount)); a.Jcc(0x87, "counts");
        Load(C(0x8C)); a.Hex("8B008B15"); a.Word(C(0x3C)); a.Hex("8B1229D0"); a.Jcc(0x82, "counts");
        a.Hex("3B05"); a.Word(C(0x30)); a.Jcc(0x82, "counts");
        a.Hex("3B05"); a.Word(C(HumanCount)); a.Jcc(0x87, "counts");
        Load(C(TeamSize)); a.Hex("8B002B05"); a.Word(C(BotCount)); a.Jcc(0x82, "counts");
        a.Hex("3B05"); a.Word(C(0x30)); a.Jcc(0x82, "counts");
        Cmp(C(Status), Registered); a.Jcc(0x84, "done");
        // No native calls occur between these checks and commit on the single game thread. The registration bit is a nonrepeatable terminal state.
        Load(C(0x8C)); a.Hex("FF00");
        Load(C(0x3C)); a.Hex("FF00");
        Set(C(Status), Registered); a.Jmp("done");
        foreach (var error in new[] { ("world", WorldChanged), ("started", MatchAlreadyStarted), ("identity", InvalidIdentity), ("counts", InvalidCounts) })
        {
            a.Label(error.Item1); Set(C(Status), error.Item2); a.Jmp("done");
        }

        a.Label("idle"); a.Hex("F0FF05"); a.Word(C(IdleAck));
        a.Label("done"); a.Hex("5B");

        return a.Finish();
    }

    private sealed class Assembly
    {
        private readonly List<byte> code = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Offset, string Label)> branches = [];
        public void Hex(string value) => code.AddRange(Convert.FromHexString(value));
        public void Bytes(byte[] value) => code.AddRange(value);
        public void Word(uint value)
        {
            byte[] bytes = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            code.AddRange(bytes);
        }
        public void Label(string name) => labels.Add(name, code.Count);
        public void Jmp(string label) { Hex("E9"); Branch(label); }
        public void Jcc(byte condition, string label) { Hex("0F" + condition.ToString("X2")); Branch(label); }
        private void Branch(string label) { branches.Add((code.Count, label)); Word(0); }
        public byte[] Finish()
        {
            byte[] result = code.ToArray();

            foreach (var branch in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch.Offset), labels[branch.Label] - branch.Offset - 4);

            return result;
        }
    }
}
