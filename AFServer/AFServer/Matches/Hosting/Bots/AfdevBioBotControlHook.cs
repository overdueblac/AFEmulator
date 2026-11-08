using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Initializes native control bits once on the existing game-thread entry point for an exact unregistered
/// BIO controller without a Pawn. The page's terminal state is nonreusable. Changes only bIsRobot, without
/// spawning, player-count registration, or account entry points.
/// </summary>
internal static class AfdevBioBotControlHook
{
    public const int PageSize = 4096;
    public const uint Request = 0;
    public const uint Started = 4;
    public const uint Returned = 8;
    public const uint Status = 0x0C;
    public const uint IdleAck = 0x10;
    public const uint Applied = 0x14;
    public const uint BeforeWord = 0x18;
    public const uint AfterWord = 0x1C;
    public const uint BeforeThread = 0x20;
    public const uint AfterThread = 0x24;
    public const uint BeforePawn = 0x28;
    public const uint AfterPawn = 0x2C;
    public const uint BeforeCounts = 0x30;
    public const uint AfterCounts = 0x40;
    public const uint BeforeState = 0x50;
    public const uint AfterState = 0x54;
    public const uint Published = 0x58;
    public const uint ThreadId = 0x60;
    public const uint Engine = 0x64;
    public const uint World = 0x68;
    public const uint WorldInfo = 0x6C;
    public const uint Game = 0x70;
    public const uint Controller = 0x74;
    public const uint Pri = 0x78;
    public const uint OnlinePage = 0x7C;
    public const uint GameFrame = 0x80;
    public const uint GameState = 0x84;
    public const uint ControllerFrame = 0x88;
    public const uint ControllerState = 0x8C;
    public const uint NetDriver = 0x90;
    public const uint RobotAddress = 0x94;
    public const uint RobotMask = 0x98;
    public const uint PawnAddress = 0x9C;
    public const uint PlayerAddress = 0xA0;
    public const uint UinAddress = 0xA4;
    public const uint UinCount = 0xA8;
    public const uint CountAddresses = 0xAC;
    public const uint IdentityCount = 0xBC;
    public const uint ProofCount = 0xC0;
    public const uint PreparationPage = 0xC4;
    public const uint IdentityBinding = 0x100;
    public const uint OnlineDescription = 0x160;
    public const uint ExpectedUin = 0x200;
    public const uint Identities = 0x300;
    public const uint Proofs = 0x600;
    public const int IdentityCapacity = 32;
    public const int ProofCapacity = 208;
    public const uint Success = 1;

    /// <summary>
    /// Appends before Tick after register and floating-point saves. The activation gate must still be
    /// appended last.
    /// </summary>
    public static byte[] Append(byte[] wrapper, uint command)
    {
        byte[] anchor = Convert.FromHexString("0FAE0C2489DC619DFF742408FF742408E8");
        int insertion = wrapper.AsSpan().IndexOf(anchor);

        if (command < 0x10000 || (command & 3) != 0 || insertion < 0 ||
            wrapper.AsSpan(insertion + 1).IndexOf(anchor) >= 0)
            throw new InvalidDataException("BIO 控制初始化缺少唯一的自有 Tick 保存边界或描述页。");

        byte[] body = BuildBody(command);
        byte[] result = [.. wrapper.AsSpan(0, insertion), .. body, .. wrapper.AsSpan(insertion)];
        int displacement = insertion + body.Length + anchor.Length;
        int original = BinaryPrimitives.ReadInt32LittleEndian(wrapper.AsSpan(insertion + anchor.Length, 4));

        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(displacement, 4), checked(original - body.Length));
        return result;
    }

    /// <summary>
    /// Returns an independently simulatable gate body. The outer wrapper retains floating-point state; this
    /// gate saves/restores integer registers.
    /// </summary>
    internal static byte[] BuildBody(uint command)
    {
        var a = new Assembler();

        uint C(uint offset) => checked(command + offset);
        void Load(uint address) { a.Hex("A1"); a.Word(address); }
        void Store(uint address) { a.Hex("A3"); a.Word(address); }
        void Set(uint offset, uint value) { a.Hex("C705"); a.Word(C(offset)); a.Word(value); }
        void Compare(uint address, uint value) { a.Hex("813D"); a.Word(address); a.Word(value); }
        void Equal(uint address, string fail) { a.Hex("3B05"); a.Word(address); a.Jcc(0x85, fail); }
        void Pointer(string fail)
        {
            a.Hex("3D00000100"); a.Jcc(0x82, fail);
            a.Hex("3D0000F0FF"); a.Jcc(0x83, fail);
            a.Hex("A903000000"); a.Jcc(0x85, fail);
        }

        void CheckThread(uint result, string fail)
        {
            a.Hex("FF15"); a.Word(0x019F6228);
            Store(C(result)); Equal(C(ThreadId), fail);
            Load(0x01FE22C0); Equal(C(ThreadId), fail);
            Compare(0x01FE22C4, 1); a.Jcc(0x85, fail);
        }

        void CheckWorld(string fail)
        {
            foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
            {
                Compare(address, 0); a.Jcc(0x85, fail);
            }

            Load(0x02063820); Pointer(fail); Equal(C(Engine), fail);
            a.Hex("83B87806000000"); a.Jcc(0x85, fail);
            Load(0x02066BF8); Equal(C(World), fail);
            a.Hex("8B80D8000000"); Equal(C(NetDriver), fail);
        }

        void CheckIdentities(string fail, string prefix)
        {
            Load(C(IdentityCount)); a.Hex("85C0"); a.Jcc(0x84, fail);
            a.Hex("83F820"); a.Jcc(0x87, fail);
            a.Hex("89C7BE"); a.Word(C(Identities));
            a.Label(prefix + "_ids");
            a.Hex("8B06"); Pointer(fail);
            a.Hex("8B56043B15"); a.Word(0x02053EAC); a.Jcc(0x83, fail);
            a.Hex("8B0D"); a.Word(0x02053EA8);
            a.Hex("85C9"); a.Jcc(0x84, fail);
            a.Hex("390491"); a.Jcc(0x85, fail);

            foreach ((byte offset, byte expected) in new (byte, byte)[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
            {
                a.Hex("8B50" + offset.ToString("X2") + "3B56" + expected.ToString("X2"));
                a.Jcc(0x85, fail);
            }

            a.Hex("F7400C00000020"); a.Jcc(0x85, fail);
            a.Hex("83C6184F"); a.Jcc(0x85, prefix + "_ids");
        }

        void CheckProofs(string fail, string prefix)
        {
            Load(C(ProofCount)); a.Hex("85C0"); a.Jcc(0x84, fail);
            a.Hex("3D"); a.Word(ProofCapacity); a.Jcc(0x87, fail);
            a.Hex("89C7BE"); a.Word(C(Proofs));
            a.Label(prefix + "_proofs");
            a.Hex("8B06"); Pointer(fail);
            a.Hex("8B002346083B4604"); a.Jcc(0x85, fail);
            a.Hex("83C60C4F"); a.Jcc(0x85, prefix + "_proofs");
        }

        void CheckOnline(string fail, string prefix)
        {
            Load(C(OnlinePage)); Pointer(fail);
            a.Hex("8BD8833B01"); a.Jcc(0x85, fail);
            a.Hex("837B0400"); a.Jcc(0x85, fail);

            // Compare the complete final-confirmation GUID, world generation, process creation time, and static identity descriptor.
            foreach ((uint onlineOffset, uint frozenOffset, int bytes) in new[]
            {
                (0x100u, IdentityBinding, 0x50), (0x40u, OnlineDescription, 0x50)
            })
            {
                for (int offset = 0; offset < bytes; offset += 4)
                {
                    a.Hex("8B83"); a.Word(onlineOffset + (uint)offset);
                    Equal(C(frozenOffset + (uint)offset), fail);
                }
            }

            a.Hex("8DBB00020000BE"); a.Word(C(ExpectedUin));
            Load(C(UinCount)); a.Hex("89C1");
            a.Label(prefix + "_online_uin");
            a.Hex("668B07663B06"); a.Jcc(0x85, fail);
            a.Hex("83C70283C60249"); a.Jcc(0x85, prefix + "_online_uin");
        }

        void CheckUin(string fail, string prefix)
        {
            Load(C(UinCount)); a.Hex("83F801"); a.Jcc(0x8C, fail);
            a.Hex("83F821"); a.Jcc(0x8F, fail);
            Load(C(UinAddress)); Pointer(fail);
            a.Hex("8B480485C9"); a.Jcc(0x8E, fail);
            a.Hex("3B0D"); a.Word(C(UinCount)); a.Jcc(0x85, fail);
            a.Hex("8B50083BD1"); a.Jcc(0x8C, fail);
            a.Hex("81FA00000100"); a.Jcc(0x8D, fail);
            a.Hex("8B00"); Pointer(fail);
            a.Hex("89C7BE"); a.Word(C(ExpectedUin));
            a.Label(prefix + "_uin");
            a.Hex("668B07663B06"); a.Jcc(0x85, fail);
            a.Hex("83C70283C60249"); a.Jcc(0x85, prefix + "_uin");
        }

        void CheckWaiting(uint pawnReceipt, uint stateReceipt, string fail)
        {
            Load(C(PawnAddress)); a.Hex("8B00"); Store(C(pawnReceipt));
            a.Hex("85C0"); a.Jcc(0x85, fail);
            Load(C(PlayerAddress)); a.Hex("833800"); a.Jcc(0x85, fail);
            Load(C(Controller)); a.Hex("8B4018"); Equal(C(ControllerFrame), fail);
            a.Hex("8B50143B15"); a.Word(C(Controller)); a.Jcc(0x85, fail);
            a.Hex("8B4028"); Store(C(stateReceipt)); Equal(C(ControllerState), fail);
            Load(C(Game)); a.Hex("8B4018"); Equal(C(GameFrame), fail);
            a.Hex("8B50143B15"); a.Word(C(Game)); a.Jcc(0x85, fail);
            a.Hex("8B4028"); Equal(C(GameState), fail);

            foreach (uint slot in new[] { Controller, Pri, Game })
            {
                Load(C(slot)); a.Hex("F780B000000008000000"); a.Jcc(0x85, fail);
            }
        }

        void CheckCounts(uint receipt, string fail)
        {
            string empty = "empty_counts_" + receipt.ToString("X2");
            string complete = "checked_counts_" + receipt.ToString("X2");

            for (uint index = 0; index < 4; index++)
            {
                Load(C(CountAddresses + index * 4)); a.Hex("8B00"); Store(C(receipt + index * 4));
            }

            // Only explicit binding to this round's preparation page allows interleaving human loading. Legacy diagnostics still require zero players and connections.
            Compare(C(PreparationPage), 0); a.Jcc(0x84, empty);
            a.Bytes(AfdevBioBotPreparationHook.BuildGuard(C(PreparationPage), C(World), C(Game)));
            a.Hex("83F801"); a.Jcc(0x85, fail);
            Compare(C(receipt + 4), 0); a.Jcc(0x85, fail);

            if (receipt == AfterCounts)
            {
                for (uint index = 0; index < 4; index++)
                {
                    Load(C(AfterCounts + index * 4)); Equal(C(BeforeCounts + index * 4), fail);
                }
            }

            a.Jmp(complete);
            a.Label(empty);

            for (uint index = 0; index < 4; index++)
            {
                Compare(C(receipt + index * 4), 0); a.Jcc(0x85, fail);
            }

            Load(C(NetDriver)); a.Hex("83784800"); a.Jcc(0x85, fail);
            a.Label(complete);
        }

        a.Hex("9C60");
        Compare(C(Request), 0); a.Jcc(0x84, "idle");
        Compare(C(Started), 0); a.Jcc(0x85, "done");
        Compare(C(Returned), 0); a.Jcc(0x85, "done");
        Set(Started, 1);
        Compare(C(Request), 1); a.Jcc(0x85, "request");
        Compare(C(Published), 1); a.Jcc(0x85, "request");
        Compare(C(Applied), 0); a.Jcc(0x85, "request");
        Compare(C(Status), 0); a.Jcc(0x85, "request");
        CheckThread(BeforeThread, "thread");
        CheckWorld("world");
        CheckIdentities("identity", "before");
        CheckProofs("metadata", "before");
        CheckUin("identity", "before");
        CheckOnline("identity", "before");
        CheckWaiting(BeforePawn, BeforeState, "identity");
        CheckCounts(BeforeCounts, "counts");
        Compare(C(RobotMask), 0x40000); a.Jcc(0x85, "metadata");
        Load(C(Controller)); a.Hex("05E8010000"); Equal(C(RobotAddress), "metadata");
        a.Hex("8B08");
        a.Hex("890D"); a.Word(C(BeforeWord));
        a.Hex("F7C100000400"); a.Jcc(0x85, "already");

        // The only game-object write. Read the source DWORD once on the current game thread, preserving all adjacent bits.
        a.Hex("81C9000004008908");
        Set(Applied, 1);
        a.Hex("8B00"); Store(C(AfterWord));
        CheckThread(AfterThread, "changed");
        CheckWorld("changed");
        CheckIdentities("changed", "after");
        CheckProofs("changed", "after");
        CheckUin("changed", "after");
        CheckOnline("changed", "after");
        CheckWaiting(AfterPawn, AfterState, "changed");
        CheckCounts(AfterCounts, "changed");
        Load(C(RobotAddress)); a.Hex("8B00"); Equal(C(AfterWord), "changed");
        Load(C(BeforeWord)); a.Hex("0D00000400"); Equal(C(AfterWord), "changed");
        Set(Status, Success); a.Jmp("finish");

        foreach ((string label, uint status) in new[]
        {
            ("thread", 2u), ("world", 3u), ("identity", 4u), ("metadata", 5u),
            ("counts", 6u), ("already", 7u), ("request", 8u), ("changed", 9u)
        })
        {
            a.Label(label); Set(Status, status); a.Jmp("finish");
        }

        a.Label("finish"); Set(Returned, 1); Set(Request, 0); a.Jmp("done");
        a.Label("idle"); a.Hex("F0FF05"); a.Word(C(IdleAck));
        a.Label("done"); a.Hex("619D");
        return a.Finish();
    }

    private sealed class Assembler
    {
        private readonly List<byte> bytes = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Offset, string Label)> branches = [];

        public void Hex(string value) => bytes.AddRange(Convert.FromHexString(value));
        public void Bytes(byte[] value) => bytes.AddRange(value);
        public void Word(uint value)
        {
            byte[] raw = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(raw, value);
            bytes.AddRange(raw);
        }

        public void Label(string label) => labels.Add(label, bytes.Count);
        public void Jcc(byte opcode, string label) { Hex("0F" + opcode.ToString("X2")); Branch(label); }
        public void Jmp(string label) { Hex("E9"); Branch(label); }
        private void Branch(string label) { branches.Add((bytes.Count, label)); Word(0); }
        public byte[] Finish()
        {
            byte[] result = bytes.ToArray();

            foreach ((int offset, string label) in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), labels[label] - offset - 4);

            return result;
        }
    }
}
