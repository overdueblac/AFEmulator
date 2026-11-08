namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Generates one Pawn.ReachedDestination/ReachedPoint call at the owned World.Tick return boundary without
/// publishing it. The host must fully validate reflection and identity descriptors first. Reuse requires
/// IdleAck; unknown completion requires stopping the original Job.
/// </summary>
internal static class AfdevBioBotReachHook
{
    public const int PayloadSize = 0xF00;
    public const uint CompletedOrdinal = 0xF00;
    public const uint IdleAck = 0xF04;
    public const uint StartedOrdinal = 0x18;
    public const uint ReturnedOrdinal = 0x1C;
    public const uint IdentityStart = 0x200;
    public const uint IdentityCapacity = 64;
    public const uint ProofStart = 0x800;
    public const uint ProofCapacity = 128;
    public const uint Parameters = 0xE00;
    public const uint Uin = 0xE20;
    public const uint ExpectedUinCapacity = 0xD8;
    public const uint ResultSentinel = uint.MaxValue;
    public const uint ReachedDestinationExec = 0x00953610;
    public const uint ReachedPointExec = 0x00953680;
    public const uint PointMode = 0xC4;
    public const uint Point = 0xE60;

    /// <summary>
    /// Shares saved integer and floating-point state with other post-Tick gates without another World.Tick
    /// call. Status 1 means a valid result for this ordinal, 2 means rejection before calling, and 3 means
    /// return with untrusted result or identity.
    /// </summary>
    public static byte[] Append(byte[] wrapper, uint trace, uint command)
    {
        byte[] tail = TickHook.Build(0, trace).AsSpan(0xBC).ToArray();
        int insertion = wrapper.Length - tail.Length;

        if (insertion < 0 || !wrapper.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("到达查询缺少已核验的 Tick 返回边界。");

        var a = new AfdevMotionAssembly();

        uint C(uint offset) => command + offset;
        void Equal(uint address, string failed) => a.Equal(address, failed);
        void Nonzero(string failed)
        {
            a.Hex("85C0"); a.Jcc(0x84, failed);
        }

        void Pair(uint addressSlot, uint valueSlot, string failed)
        {
            a.Load(C(addressSlot)); a.Hex("8B00"); Equal(C(valueSlot), failed);
        }

        // This proof set covers live UObject identity, reflected properties, function scripts, virtual slots, and the complete frozen isolation-identity page.
        // The host generates each proof individually; pointer equality or a UIN prefix cannot replace provenance checks.
        void VerifyIdentity(string failed)
        {
            foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
            {
                a.Compare(address, 0); a.Jcc(0x85, failed);
            }

            a.Hex("F705"); a.Word(0x01FCE998); a.Word(1); a.Jcc(0x85, failed);
            a.Hex("FF15"); a.Word(0x019F6228); Equal(C(0x40), failed);
            a.Load(0x01FE22C0); Equal(C(0x40), failed);
            a.Compare(0x01FE22C4, 1); a.Jcc(0x85, failed);
            a.Load(0x02063820); Equal(C(0x44), failed);
            a.Hex("83B87806000000"); a.Jcc(0x85, failed);
            a.Load(0x02066BF8); Equal(C(0x48), failed);
            a.Hex("8B4050"); Nonzero(failed);
            a.Hex("83784000"); a.Jcc(0x8E, failed);
            a.Hex("8B403C"); Nonzero(failed);
            a.Hex("8B00"); Equal(C(0x4C), failed);
            a.Hex("8B8014040000"); Equal(C(0x50), failed);

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

            foreach ((uint address, uint value) in new[]
            {
                (0x90u, 0x58u), (0x94u, 0x54u), (0x98u, 0x5Cu), (0x9Cu, 0x54u),
                (0xA0u, 0x4Cu), (0xA4u, 0x4Cu), (0xA8u, 0x4Cu)
            })
                Pair(address, value, failed);

            a.Load(C(0x8C)); a.Hex("833800"); a.Jcc(0x85, failed);
            a.Load(C(0x84)); a.Hex("8B002305"); a.Word(C(0x88)); Nonzero(failed);
            a.Load(C(0xB0)); a.Hex("833800"); a.Jcc(0x8E, failed);

            foreach (uint slot in new[] { 0x54u, 0x58u, 0x5Cu })
            {
                a.Load(C(slot)); a.Hex("F780B000000008000000"); a.Jcc(0x85, failed);
            }

            // Coordinates have no dereferenceable Actor target; retain complete Pawn/PC/world proofs for the caller itself.
            a.Compare(C(PointMode), 0); a.Jcc(0x85, "target-checked-" + failed);
            Pair(0xAC, 0x4C, failed);
            a.Load(C(0x60)); a.Hex("F780B000000008000000"); a.Jcc(0x85, failed);
            // A Pawn target must remain live and owned by the same controller before and after calling. Navigation points skip this branch.
            a.Compare(C(0xB4), 0); a.Jcc(0x84, "target-checked-" + failed);
            a.Load(C(0xB8)); a.Hex("833800"); a.Jcc(0x8E, failed);
            Pair(0xBC, 0xB4, failed);
            Pair(0xC0, 0x60, failed);
            a.Label("target-checked-" + failed);
        }

        void VerifyUin(string failed)
        {
            a.Load(C(0xD0)); a.Hex("8B48043B0D"); a.Word(C(0xD4)); a.Jcc(0x85, failed);
            a.Hex("83F911"); a.Jcc(0x85, failed); // Count must be exactly 17 characters, including the terminator.
            a.Hex("8B500885D2"); a.Jcc(0x8E, failed);
            a.Hex("81FA00040000"); a.Jcc(0x8F, failed); // Capacity is signed and must be within 1..1024.
            a.Hex("3BCA"); a.Jcc(0x8F, failed); // Compare signed Count <= Capacity; negative capacity must not become a huge unsigned value.
            a.Hex("3B15"); a.Word(C(ExpectedUinCapacity)); a.Jcc(0x85, failed);
            a.Hex("8B3085F6"); a.Jcc(0x84, failed);
            a.Hex("BF"); a.Word(C(Uin)); a.Hex("FCF366A7"); a.Jcc(0x85, failed);
        }

        void VerifyFunction(string failed)
        {
            a.Compare(C(PointMode), 1); a.Jcc(0x84, "point-function-" + failed);
            a.Compare(C(PointMode), 0); a.Jcc(0x85, failed);
            a.Compare(C(0x68), ReachedDestinationExec); a.Jcc(0x85, failed);
            a.Load(C(0x64)); a.Hex("6683B8B000000000"); a.Jcc(0x85, failed);
            a.Hex("6683B8BE00000008"); a.Jcc(0x85, failed);
            a.Hex("6683B8C000000004"); a.Jcc(0x85, failed);
            a.Hex("83785008"); a.Jcc(0x85, failed);
            a.Hex("8378580B"); a.Jcc(0x85, failed);
            a.Load(C(Parameters)); Equal(C(0x60), failed);
            a.Jmp("common-function-" + failed);

            a.Label("point-function-" + failed);
            a.Compare(C(0x60), 0); a.Jcc(0x85, failed);
            a.Compare(C(0xB4), 0); a.Jcc(0x85, failed);
            a.Compare(C(0x68), ReachedPointExec); a.Jcc(0x85, failed);
            a.Load(C(0x64)); a.Hex("6683B8B000000000"); a.Jcc(0x85, failed);
            a.Hex("6683B8BE00000014"); a.Jcc(0x85, failed);
            a.Hex("6683B8C000000010"); a.Jcc(0x85, failed);
            a.Hex("83785014"); a.Jcc(0x85, failed);
            a.Hex("83785814"); a.Jcc(0x85, failed);

            for (uint axis = 0; axis < 12; axis += 4)
            {
                a.Load(C(Parameters + axis)); Equal(C(Point + axis), failed);
            }

            a.Compare(C(Parameters + 12), 0); a.Jcc(0x85, failed);
            a.Label("common-function-" + failed);
            a.Load(C(0x64));
            a.Hex("81B8AC00000000040200"); a.Jcc(0x85, failed);
            a.Hex("8B80C8000000"); Equal(C(0x68), failed);
            a.Load(C(0x5C)); a.Hex("8B0081B820010000"); a.Word(QueryHook.ActorProcessEvent); a.Jcc(0x85, failed);
        }

        void LoadResult(string suffix)
        {
            a.Compare(C(PointMode), 1); a.Jcc(0x84, "point-result-" + suffix);
            a.Load(C(Parameters + 4)); a.Jmp("result-loaded-" + suffix);
            a.Label("point-result-" + suffix);
            a.Load(C(Parameters + 16));
            a.Label("result-loaded-" + suffix);
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

        a.Compare(C(0), 1); a.Jcc(0x85, "rejected");
        a.Compare(C(12), 0); a.Jcc(0x85, "rejected");
        a.Compare(C(StartedOrdinal), 0); a.Jcc(0x85, "rejected");
        a.Compare(C(ReturnedOrdinal), 0); a.Jcc(0x85, "rejected");
        a.Compare(C(0x10), ResultSentinel); a.Jcc(0x85, "rejected");
        LoadResult("before"); a.Hex("3D"); a.Word(ResultSentinel); a.Jcc(0x85, "rejected");
        a.Compare(C(CompletedOrdinal), uint.MaxValue); a.Jcc(0x84, "rejected");
        a.Load(C(CompletedOrdinal)); a.Hex("40"); Equal(C(0x80), "rejected");
        VerifyIdentity("rejected");
        VerifyUin("rejected");
        VerifyFunction("rejected");

        // Started and Returned ordinals are independent of valid completion. Failure cannot advance CompletedOrdinal; the host must not retry a failed page.
        a.Load(C(0x80)); a.Store(C(StartedOrdinal));
        a.Set(C(4), 1);
        a.Hex("8925"); a.Word(C(0x20));
        a.Hex("6A0068"); a.Word(C(Parameters));
        a.Hex("FF35"); a.Word(C(0x64));
        a.Hex("8B0D"); a.Word(C(0x5C));
        a.Hex("B8"); a.Word(QueryHook.ActorProcessEvent); a.Hex("FFD0");
        a.Hex("8925"); a.Word(C(0x24));
        a.Load(C(StartedOrdinal)); a.Store(C(ReturnedOrdinal));
        Equal(C(0x80), "changed");
        a.Load(C(0x20)); Equal(C(0x24), "changed");
        a.Compare(C(4), 1); a.Jcc(0x85, "changed");
        a.Compare(C(8), 0); a.Jcc(0x85, "changed");
        a.Compare(C(12), 0); a.Jcc(0x85, "changed");
        a.Compare(C(0x10), ResultSentinel); a.Jcc(0x85, "changed");
        VerifyIdentity("changed");
        VerifyUin("changed");
        VerifyFunction("changed");
        LoadResult("returned"); a.Hex("83F801"); a.Jcc(0x87, "changed");
        a.Store(C(0x10));
        a.Load(C(StartedOrdinal)); a.Store(C(CompletedOrdinal));
        a.Set(C(12), 1); a.Jmp("complete");
        a.Label("rejected"); a.Set(C(12), 2); a.Jmp("complete");
        a.Label("changed"); a.Set(C(12), 3);
        a.Label("complete"); a.Set(C(8), 1);
        a.Label("done");
        return [.. wrapper.AsSpan(0, insertion), .. a.Finish(), .. tail];
    }
}
