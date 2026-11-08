namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Converts engine navigation direction to this game's walking input, following
/// TGPlayerController.PlayerWalking.PlayerMove. Forward input uses MaxForwardAccel × fMovingScale; native
/// physics owns speed caps, friction, damage slowdown, and collision. Runs only before same-generation
/// game-thread physics, preserving AccelRate, GroundSpeed, Velocity, and world time.
/// </summary>
internal static class AfdevBioBotMovementInput
{
    public const uint Acceleration = 0x108;
    public const uint MaxForwardAccel = 0x10C;
    public const uint MovingScale = 0x110;
    public const uint Physics = 0x114;
    public const uint IgnoreMoveInput = 0x118;
    public const uint MappedSteps = 0x11C;
    public const uint SourceMagnitude = 0x120;
    public const uint RequestedMagnitude = 0x124;

    /// <summary>
    /// The caller has validated objects, properties, and latent state and saved all
    /// registers/floating-point state. Preserve its EBX stack anchor.
    /// </summary>
    public static byte[] Build(uint command)
    {
        var a = new AfdevMotionAssembly();

        uint C(uint offset) => command + offset;
        void Finite(byte register)
        {
            // After MOVD EAX,XMMn, check the absolute IEEE single-precision exponent. Invalid input must not reach native normalization.
            a.Hex("660F7E" + (0xC0 + register * 8).ToString("X2"));
            a.Hex("25FFFFFF7F3D0000807F"); a.Jcc(0x83, "input-done");
        }

        a.Load(C(Acceleration)); a.Hex("89C1");
        a.Load(C(IgnoreMoveInput)); a.Hex("803800"); a.Jcc(0x85, "input-locked");
        a.Load(C(Physics)); a.Hex("803801"); a.Jcc(0x85, "input-done");

        a.Hex("F30F1001F30F104904F30F105108"); // Direction supplied by native navigation.
        Finite(0); Finite(1); Finite(2);
        a.Hex("0F28D8F30F59D80F28E1F30F59E1F30F58DC0F28E2F30F59E2F30F58DC");
        Finite(3);
        a.Hex("85C0"); a.Jcc(0x84, "input-done"); // Never invent a new direction for stopped navigation.
        a.Hex("F30F51DBF30F111D"); a.Word(C(SourceMagnitude));

        a.Load(C(MaxForwardAccel)); a.Hex("8B1085D2"); a.Jcc(0x8E, "input-done");
        a.Hex("F30F1020"); Finite(4);
        a.Load(C(MovingScale)); a.Hex("8B1085D2"); a.Jcc(0x88, "input-done");
        a.Hex("F30F1028"); Finite(5);
        a.Hex("F30F59E5"); Finite(4);
        a.Hex("F30F1125"); a.Word(C(RequestedMagnitude));
        a.Hex("F30F5EE3F30F59C4F30F59CCF30F59D4");
        Finite(0); Finite(1); Finite(2);

        // Normalize direction afresh per call so repeated calls do not multiply prior input again. Preserve existing airborne physics until landing.
        a.Hex("F30F1101F30F114904F30F115108");
        a.Increment(C(MappedSteps)); a.Jmp("input-done");
        a.Label("input-locked");
        a.Hex("C70100000000C7410400000000C7410800000000");
        a.Label("input-done");
        return a.Finish();
    }
}
