namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Executes one BIO combat script serially at the existing World.Tick return boundary. Reuses a fixed
/// command page; completion ordinals mean only request retirement. Started/Returned and status jointly
/// prove actual execution. Rejecting an old Pawn or weapon must not masquerade as successful firing.
/// </summary>
internal static class AfdevBioBotCombatHook
{
    public const int PageSize = 0x4000;
    public const int PayloadSize = 0x3F00;
    public const uint CompletedOrdinal = 0x3F00;
    public const uint IdleAck = 0x3F04;
    public const uint IdentityStart = 0x200;
    public const uint IdentityCapacity = 160;
    public const uint ProofStart = 0x1200;
    public const uint ProofCapacity = 512;
    public const uint Parameters = 0x3D00;
    public const uint Uin = 0x3E00;

    /// <summary>
    /// Appends only to the owned post-Tick save boundary, without installing another global hook or
    /// changing world-frame progression.
    /// </summary>
    public static byte[] Append(byte[] wrapper, uint trace, uint command)
    {
        byte[] tail = TickHook.Build(0, trace).AsSpan(0xBC).ToArray();
        int insertion = wrapper.Length - tail.Length;

        if (insertion < 0 || !wrapper.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("战斗命令缺少已核验的 Tick 返回边界。");

        var a = new AfdevMotionAssembly();

        uint C(uint offset) => command + offset;
        void Nonzero(string failed)
        {
            a.Hex("85C0"); a.Jcc(0x84, failed);
        }

        void Verify(string failed)
        {
            foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
            {
                a.Compare(address, 0); a.Jcc(0x85, failed);
            }

            a.Hex("F705"); a.Word(0x01FCE998); a.Word(1); a.Jcc(0x85, failed);
            a.Hex("FF15"); a.Word(0x019F6228); a.Equal(C(0x40), failed);
            a.Load(0x01FE22C0); a.Equal(C(0x40), failed);
            a.Compare(0x01FE22C4, 1); a.Jcc(0x85, failed);
            a.Load(0x02063820); a.Equal(C(0x44), failed);
            a.Hex("83B87806000000"); a.Jcc(0x85, failed);
            a.Load(0x02066BF8); a.Equal(C(0x48), failed);
            a.Hex("8B4050"); Nonzero(failed);
            a.Hex("83784000"); a.Jcc(0x8E, failed);
            a.Hex("8B403C"); Nonzero(failed);
            a.Hex("8B00"); a.Equal(C(0x4C), failed);
            a.Hex("8B8014040000"); a.Equal(C(0x50), failed);

            a.Load(C(0x78)); Nonzero(failed);
            a.Hex("3D"); a.Word(IdentityCapacity); a.Jcc(0x87, failed);
            a.Hex("89C7BE"); a.Word(C(IdentityStart));
            a.Label("identities-" + failed);
            a.Hex("8B068B560485D2"); a.Jcc(0x88, failed);
            a.Hex("3B15"); a.Word(0x02053EAC); a.Jcc(0x8D, failed);
            a.Hex("8B0D"); a.Word(0x02053EA8); a.Hex("85C9"); a.Jcc(0x84, failed);
            a.Hex("390491"); a.Jcc(0x85, failed);

            foreach ((int field, int expected) in new[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
            {
                a.Hex("8B50" + field.ToString("X2") + "3B56" + expected.ToString("X2"));
                a.Jcc(0x85, failed);
            }

            a.Hex("F7400C00000020"); a.Jcc(0x85, failed);
            a.Hex("83C6184F"); a.Jcc(0x85, "identities-" + failed);
            a.Load(C(0x7C)); Nonzero(failed);
            a.Hex("3D"); a.Word(ProofCapacity); a.Jcc(0x87, failed);
            a.Hex("89C7BE"); a.Word(C(ProofStart));
            a.Label("proofs-" + failed);
            a.Hex("8B068B002346083B4604"); a.Jcc(0x85, failed);
            a.Hex("83C60C4F"); a.Jcc(0x85, "proofs-" + failed);

            // Recheck the complete UTF-16 UIN. Count/Capacity must be nonnegative and within bounds.
            a.Load(C(0x90)); a.Hex("8B480483F911"); a.Jcc(0x85, failed);
            a.Hex("8B500885D2"); a.Jcc(0x8E, failed);
            a.Hex("81FA00040000"); a.Jcc(0x8F, failed);
            a.Hex("3BCA"); a.Jcc(0x8F, failed);
            a.Hex("3B15"); a.Word(C(0x94)); a.Jcc(0x85, failed);
            a.Hex("8B3085F6"); a.Jcc(0x84, failed);
            a.Hex("BF"); a.Word(C(Uin)); a.Hex("FCF366A7"); a.Jcc(0x85, failed);

            a.Load(C(0x64)); a.Hex("6683B8B000000000"); a.Jcc(0x85, failed);
            a.Hex("81B8C800000010DB4800"); a.Jcc(0x85, failed);
            a.Load(C(0x68)); a.Hex("8B0081B820010000"); a.Word(QueryHook.ActorProcessEvent); a.Jcc(0x85, failed);

            // StopFire requires only the same equipment, allowing a dead or newly frozen Pawn to clear native input and pending fire.
            a.Compare(C(0x84), (uint)AfdevBioBotCombatAction.StopFire); a.Jcc(0x84, "health-done-" + failed);
            a.Load(C(0x98)); a.Hex("833800"); a.Jcc(0x8E, failed);
            a.Load(C(0x9C)); a.Hex("85C0"); a.Jcc(0x84, "health-done-" + failed);
            a.Hex("833800"); a.Jcc(0x8E, failed);
            a.Label("health-done-" + failed);
        }

        a.Compare(C(0), 0); a.Jcc(0x85, "requested");
        a.Increment(C(IdleAck)); a.Jmp("done");
        a.Label("requested");
        a.Compare(C(8), 0); a.Jcc(0x85, "done");
        a.Compare(C(4), 0); a.Jcc(0x85, "done");

        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
        {
            a.Compare(address, 0); a.Jcc(0x85, "done");
        }

        a.Compare(C(0), 1); a.Jcc(0x85, "fault");
        a.Compare(C(CompletedOrdinal), uint.MaxValue); a.Jcc(0x84, "fault");
        a.Load(C(CompletedOrdinal)); a.Hex("40"); a.Equal(C(0x80), "fault");
        a.Compare(C(0x18), 0); a.Jcc(0x85, "fault");
        a.Compare(C(0x1C), 0); a.Jcc(0x85, "fault");
        Verify("rejected");

        a.Load(C(0x80)); a.Store(C(0x18));
        a.Load(trace + 4); a.Store(C(0x28));
        a.Set(C(4), 1);
        a.Hex("8925"); a.Word(C(0x20));
        a.Hex("6A0068"); a.Word(C(Parameters));
        a.Hex("FF35"); a.Word(C(0x64));
        a.Hex("8B0D"); a.Word(C(0x68));
        a.Hex("B8"); a.Word(QueryHook.ActorProcessEvent); a.Hex("FFD0");
        a.Hex("8925"); a.Word(C(0x24));
        a.Load(C(0x18)); a.Store(C(0x1C));
        a.Load(trace + 4); a.Store(C(0x2C));
        a.Load(C(0x20)); a.Equal(C(0x24), "fault");
        a.Hex("FF15"); a.Word(0x019F6228); a.Equal(C(0x40), "fault");
        Verify("changed");
        a.Compare(C(0x84), (uint)AfdevBioBotCombatAction.LineOfSight); a.Jcc(0x85, "valid");
        a.Load(C(Parameters + 4)); a.Hex("83F801"); a.Jcc(0x87, "fault");
        a.Store(C(0x10));
        a.Label("valid"); a.Set(C(12), 1); a.Jmp("retired");
        a.Label("rejected"); a.Set(C(12), 2); a.Jmp("retired");
        a.Label("changed"); a.Set(C(12), 3); a.Jmp("retired");
        a.Label("fault"); a.Set(C(12), 4); a.Jmp("complete");
        a.Label("retired"); a.Load(C(0x80)); a.Store(C(CompletedOrdinal));
        a.Label("complete"); a.Set(C(8), 1);
        a.Label("done");
        return [.. wrapper.AsSpan(0, insertion), .. a.Finish(), .. tail];
    }
}
