using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Completes ordinary script-call dispatch through this round's start gate. CallFunction parses arguments
/// then calls ProcessInternal directly, bypassing UFunction's execution pointer. Only this exact world's
/// PendingMatch.StartMatch enters the existing preparation gate.
/// </summary>
internal static class AfdevBioBotScriptDispatchHook
{
    // AfdevBotNativeImage validates the complete CallFunction range containing this call site.
    public const uint CallSite = 0x0048E09A;

    /// <summary>
    /// Takes the existing callee FFrame/Result and passes all registers, flags, and stack arguments
    /// unchanged to the target entry.
    /// </summary>
    public static byte[] Build(uint command, uint startEntry)
    {
        if (command < 0x10000 || (command & 3) != 0 || startEntry < 0x10000)
            throw new ArgumentException("脚本分派必须绑定本进程的准备页与开局入口。");

        var code = new List<byte>();
        var ordinaryBranches = new List<int>();

        void Hex(string value) => code.AddRange(Convert.FromHexString(value));
        void Word(uint value)
        {
            byte[] bytes = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            code.AddRange(bytes);
        }

        void OrdinaryUnlessEqual()
        {
            Hex("0F85");
            ordinaryBranches.Add(code.Count);
            Word(0);
        }

        // The hot path saves only EAX/flags; other registers and floating-point state do not participate in filtering.
        Hex("9C503B0D"); Word(command + AfdevBioBotPreparationHook.Game); OrdinaryUnlessEqual();
        // The call site has already built and consumed callee arguments. After two pushes, original FFrame* is at ESP+12.
        Hex("8B44240C8B40103B05"); Word(command + AfdevBioBotPreparationHook.StartFunction); OrdinaryUnlessEqual();
        Hex("8B44240C394814"); OrdinaryUnlessEqual();
        Hex("589D68"); Word(startEntry); Hex("C3");

        int ordinary = code.Count;

        Hex("589D68"); Word(AfdevBioBotTimerHook.OriginalExec); Hex("C3");

        byte[] result = code.ToArray();

        foreach (int branch in ordinaryBranches)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch, 4), ordinary - branch - 4);

        return result;
    }

    /// <summary>
    /// Generates the original CallFunction site's relative CALL. Argument construction and post-call
    /// cleanup remain in the original function.
    /// </summary>
    public static byte[] CallPatch(uint entry)
    {
        if (entry < 0x10000)
            throw new ArgumentOutOfRangeException(nameof(entry));

        byte[] result = new byte[5];

        result[0] = 0xE8;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1), unchecked(entry - CallSite - 5));
        return result;
    }
}
