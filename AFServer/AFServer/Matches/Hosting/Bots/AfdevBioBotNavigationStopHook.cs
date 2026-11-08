namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Action-retirement gate depending only on controller identity. Old Pawns are absent from descriptors and
/// cancellation. StopLatentExecution applies only to the exact owned latent action before Stop. If native
/// state has changed, verify the old program has no remaining references and preserve the new Code/latent
/// state.
/// </summary>
internal static class AfdevBioBotNavigationStopHook
{
    public const int PayloadSize = 0xF00;
    public const uint IdleAck = 0xF04;
    public const uint IdentityStart = 0x100;
    public const uint IdentityCapacity = 64;
    public const uint ProofStart = 0x700;
    public const uint ProofCapacity = 160;
    public const uint Parameters = 0xE80;
    public const uint NativeStateChanged = 0x24;
    public const uint ObservedNode = 0x28;
    public const uint ObservedState = 0x2C;
    public const uint ObservedLatent = 0x30;
    public const uint ExpectedLatent = 0x7C;

    public static byte[] Append(byte[] wrapper, uint trace, uint factory, uint command)
    {
        byte[] tail = TickHook.Build(0, trace).AsSpan(0xBC).ToArray();
        int insertion = wrapper.Length - tail.Length;

        if (insertion < 0 || !wrapper.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("导航退休门缺少已核验的 Tick 返回边界。");

        var a = new AfdevMotionAssembly();

        uint C(uint offset) => command + offset;
        void Equal(uint address) => a.Equal(address, "failed");
        void Nonzero() { a.Hex("85C0"); a.Jcc(0x84, "failed"); }

        void Verify(string suffix)
        {
            a.Hex("F705"); a.Word(0x01FCE998); a.Word(1); a.Jcc(0x85, "failed");
            a.Hex("FF15"); a.Word(0x019F6228); Equal(C(0x40));
            a.Load(0x01FE22C0); Equal(C(0x40));
            a.Compare(0x01FE22C4, 1); a.Jcc(0x85, "failed");
            a.Load(0x02063820); Equal(C(0x44));
            a.Hex("83B87806000000"); a.Jcc(0x85, "failed");
            a.Load(0x02066BF8); Equal(C(0x48));
            a.Hex("8B4050"); Nonzero();
            a.Hex("83784000"); a.Jcc(0x8E, "failed");
            a.Hex("8B403C"); Nonzero();
            a.Hex("8B00"); Equal(C(0x4C));
            a.Hex("8B8014040000"); Equal(C(0x50));
            a.Compare(factory + 8, 1); a.Jcc(0x85, "failed");
            a.Compare(factory + 12, 1); a.Jcc(0x85, "failed");
            a.Load(factory + 0x30); Equal(C(0x54));

            a.Load(C(0x74)); Nonzero();
            a.Hex("3D"); a.Word(IdentityCapacity); a.Jcc(0x87, "failed");
            a.Hex("89C7BE"); a.Word(C(IdentityStart));
            a.Label("identities-" + suffix);
            a.Hex("8B068B560485D2"); a.Jcc(0x88, "failed");
            a.Hex("3B15"); a.Word(0x02053EAC); a.Jcc(0x8D, "failed");
            a.Hex("8B0D"); a.Word(0x02053EA8); a.Hex("85C9"); a.Jcc(0x84, "failed");
            a.Hex("390491"); a.Jcc(0x85, "failed");

            foreach ((int field, int expected) in new[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
            {
                a.Hex("8B50" + field.ToString("X2") + "3B56" + expected.ToString("X2"));
                a.Jcc(0x85, "failed");
            }

            a.Hex("F7400C00000020"); a.Jcc(0x85, "failed");
            a.Hex("83C6184F"); a.Jcc(0x85, "identities-" + suffix);

            // Confirm PC still owns the frame before reading it. This path contains no Pawn address or metadata.
            a.Load(C(0x54)); a.Hex("F780B000000008000000"); a.Jcc(0x85, "failed");
            a.Hex("8B4018"); Equal(C(0x58)); Nonzero();
            a.Hex("8B4014"); Equal(C(0x54));
            // The native FStateFrame debug flag is one byte; padding at +0x33 does not participate in lifetime checks.
            a.Load(C(0x58)); a.Hex("80783200"); a.Jcc(0x85, "failed");

            foreach (byte offset in new byte[] { 0x34, 0x38, 0x3C })
            {
                a.Hex("8378" + offset.ToString("X2") + "00"); a.Jcc(0x85, "failed");
            }

            a.Compare(0x01FD6A08, 0); a.Jcc(0x85, "failed");
            a.Load(C(0x78)); Nonzero();
            a.Hex("3D"); a.Word(ProofCapacity); a.Jcc(0x87, "failed");
            a.Hex("89C7BE"); a.Word(C(ProofStart));
            a.Label("proofs-" + suffix);
            a.Hex("8B068B002346083B4604"); a.Jcc(0x85, "failed");
            a.Hex("83C60C4F"); a.Jcc(0x85, "proofs-" + suffix);
        }

        void ObserveFrame()
        {
            a.Load(C(0x58)); a.Hex("8B4010"); a.Store(C(ObservedNode));
            a.Load(C(0x58)); a.Hex("8B4028"); a.Store(C(ObservedState));
            a.Load(C(0x58)); a.Hex("0FB74030"); a.Store(C(ObservedLatent));
            a.Load(C(0x58)); a.Hex("8B4018"); a.Store(C(0x20));
            a.Load(C(ObservedNode)); a.Hex("3B05"); a.Word(C(0x5C)); a.Jcc(0x85, "state-changed");
            a.Load(C(ObservedState)); a.Hex("3B05"); a.Word(C(0x60)); a.Jcc(0x85, "state-changed");
            a.Load(C(0x20));
        }

        a.Compare(C(0), 0); a.Jcc(0x85, "requested");
        a.Increment(C(IdleAck)); a.Jmp("done");
        a.Label("requested");
        a.Compare(C(4), 0); a.Jcc(0x85, "done");
        a.Compare(C(0), 1); a.Jcc(0x85, "failed");

        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
        {
            a.Compare(address, 0); a.Jcc(0x85, "done");
        }

        Verify("before");
        ObserveFrame();
        a.Hex("85C0"); a.Jcc(0x84, "retired");
        a.Hex("3B05"); a.Word(C(0x64)); a.Jcc(0x84, "not-consumed");
        Equal(C(0x68));
        a.Compare(C(8), 0); a.Jcc(0x85, "wait-stop");
        a.Load(C(0x58)); a.Hex("0FB74030"); Equal(C(ExpectedLatent));
        a.Load(trace); a.Store(C(0x10));
        a.Increment(C(8));
        a.Hex("6A0068"); a.Word(C(Parameters));
        a.Hex("FF35"); a.Word(C(0x6C));
        a.Hex("8B0D"); a.Word(C(0x54));
        a.Hex("B8"); a.Word(QueryHook.ActorProcessEvent); a.Hex("FFD0");
        a.Increment(C(12));
        Verify("returned");
        ObserveFrame();
        Equal(C(0x68));
        a.Label("wait-stop");
        a.Compare(C(8), 1); a.Jcc(0x85, "failed");
        a.Compare(C(12), 1); a.Jcc(0x85, "failed");
        a.Load(C(0x58)); a.Hex("6683783000"); a.Jcc(0x85, "failed");
        a.Jmp("done");

        a.Label("not-consumed");
        a.Compare(C(8), 0); a.Jcc(0x85, "failed");
        a.Load(C(0x58)); a.Hex("6683783000"); a.Jcc(0x85, "failed");
        a.Jmp("done");

        a.Label("retired");
        a.Load(C(0x58)); a.Hex("6683783000"); a.Jcc(0x85, "failed");
        a.Jmp("acknowledge");

        // Native states such as Dead replace Node/State and Code. Here the same PC still owns the validated frame,
        // with an empty PushState stack at World.Tick return. Once the cursor leaves the complete owned program, the old action has no frame references.
        // New state may be sleeping or reviving; StopLatentExecution must not clear it.
        a.Label("state-changed");
        a.Load(C(0x20)); a.Hex("3B05"); a.Word(C(0x64)); a.Jcc(0x82, "detached");
        a.Hex("3B05"); a.Word(C(0x68)); a.Jcc(0x86, "failed");
        a.Label("detached");
        a.Set(C(NativeStateChanged), 1);

        a.Label("acknowledge");
        a.Load(C(8)); Equal(C(12));
        a.Load(trace); a.Store(C(0x14));
        a.Set(C(4), 1); a.Jmp("done");
        a.Label("failed"); a.Set(C(4), 3);
        a.Label("done");
        return [.. wrapper.AsSpan(0, insertion).ToArray(), .. a.Finish(), .. tail];
    }
}
