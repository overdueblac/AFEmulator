using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Wraps the exact PendingMatch.Timer script entry. First registers bots corresponding to humans that
/// completed PostLogin in the same frame, then always enters original ProcessInternal. Never rewrites
/// shared Script or skips human Timer/match rules.
/// </summary>
internal static class AfdevBioBotTimerHook
{
    // The current image's UFunction script entry. Full ProcessEvent auditing confirmed dispatch through +0xC8.
    public const uint OriginalExec = 0x0048DB10;
    public const uint FunctionSlot = 0x130;
    public const uint OriginalExecSlot = 0x134;
    public const uint EntrySlot = 0x138;
    public const uint InstalledSlot = 0x13C;
    public const uint EnteredSlot = 0x140;

    /// <summary>
    /// Write and read back the code page first, then place its address in EntrySlot. Request=2 asks preTick
    /// to install Func on the game thread. Wait for InstalledSlot=1 and Request=0 before publishing
    /// Request=1. Retain pages until the original Job fully stops.
    /// </summary>
    public static byte[] Build(uint command) => Build([command]);

    /// <summary>
    /// Installs the same PendingMatch.Timer once, checking each independent activation page in frozen-slot
    /// order before running native Timer.
    /// </summary>
    public static byte[] Build(IReadOnlyList<uint> commands)
    {
        if (commands.Count is 0 or > 15 || commands.Distinct().Count() != commands.Count ||
            commands.Any(command => command < 0x10000 || (command & 3) != 0))
            throw new ArgumentException("BIO Timer 必须绑定最多 15 个互不重复的激活槽。", nameof(commands));

        var code = new List<byte>();
        var branches = new List<(int Offset, int Target)>();

        void Hex(string value) => code.AddRange(Convert.FromHexString(value));
        void Word(uint value)
        {
            byte[] bytes = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            code.AddRange(bytes);
        }

        // Preserve original ECX, FFrame*, Result*, and all floating-point state. Original arguments stay on the caller's stack.
        Hex("9C6089E381EC2002000083E4F00FAE0424DBE3D92C24FC");

        foreach (uint command in commands)
        {
            var exits = new List<int>();

            void SkipUnlessEqual()
            {
                Hex("0F85");
                exits.Add(code.Count);
                Word(0);
            }

            Hex("813D"); Word(command); Word(1); SkipUnlessEqual();
            Hex("8B43183B05"); Word(command + 0x48); SkipUnlessEqual();
            // pushfd/pushad save 36 bytes; original ESP+4's FFrame lies at save-area +40.
            Hex("8B43288B50103B15"); Word(command + FunctionSlot); SkipUnlessEqual();
            Hex("8B50143B15"); Word(command + 0x48); SkipUnlessEqual();
            Hex("F0FF05"); Word(command + EnteredSlot);
            code.AddRange(AfdevBioBotActivationHook.BuildBody(command));

            foreach (int branch in exits)
                branches.Add((branch, code.Count));
        }

        Hex("0FAE0C2489DC619D");
        // The tail call preserves the original ABI and return address. The original function consumes Frame/Result.
        Hex("68"); Word(OriginalExec); Hex("C3");

        byte[] result = code.ToArray();

        foreach (var branch in branches)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch.Offset), branch.Target - branch.Offset - 4);

        return result;
    }
}
