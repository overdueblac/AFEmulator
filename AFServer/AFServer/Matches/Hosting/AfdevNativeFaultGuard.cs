using System.Buffers.Binary;
using System.Text;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Captures verified fatal resource-loading branches and remains there after publishing diagnostics for
/// owner cleanup, preventing blocking dialogs and false readiness.
/// </summary>
/// <remarks>
/// Replaces only seven MessageBoxW calls known to be followed by appRequestExit(1). Preserves success paths
/// and does not ignore loading errors.
/// </remarks>
internal sealed class AfdevNativeFaultGuard
{
    private const string MessagePrefix = "客户端文件可能已损坏或者尚未更新成功";
    private readonly AfdevOwnedProcess process;
    private readonly uint memory;
    private readonly List<(uint Address, byte[] Code)> hooks = [];
    private static readonly (uint Call, uint Text, string Branch)[] Sites =
    [
        (0x0044C0DC, 0x01A10828, "async package file unavailable (first path)"),
        (0x0044D159, 0x01A10F98, "async package file unavailable (second path)"),
        (0x0044D72E, 0x01A11A18, "async compressed data validation (first path)"),
        (0x0044D8A4, 0x01A11C40, "async compressed data validation (second path)"),
        (0x0045111E, 0x01A12308, "async compressed file header validation"),
        (0x004B567E, 0x01A20B50, "linker archive size not positive"),
        (0x00742A00, 0x01AC8FC0, "texture initial mip validation")
    ];

    internal AfdevNativeFaultGuard(AfdevOwnedProcess process)
    {
        this.process = process;

        byte[] expectedCall = Convert.FromHexString("FF1564679F01");
        byte[] expectedText = Encoding.Unicode.GetBytes(MessagePrefix);

        foreach (var site in Sites)
        {
            AfdevNativeBootstrap.Require(process.ReadMemory(site.Call, 6).AsSpan().SequenceEqual(expectedCall),
                $"原生资源错误调用锚点不匹配：0x{site.Call:X8}。");
            AfdevNativeBootstrap.Require(process.ReadMemory(site.Text, expectedText.Length).AsSpan().SequenceEqual(expectedText),
                $"原生资源错误文本锚点不匹配：0x{site.Text:X8}。");
        }

        memory = process.AllocateMemory(8192);

        for (int index = 0; index < Sites.Length; index++)
        {
            var site = Sites[index];
            uint record = memory + (uint)index * 256;
            uint trampoline = memory + 4096 + (uint)index * 128;
            var code = new List<byte>();

            void Hex(string value) => code.AddRange(Convert.FromHexString(value));
            void Word(uint value) => code.AddRange(BitConverter.GetBytes(value));
            void Store(uint address, uint value) { Hex("C705"); Word(address); Word(value); }

            // PUSHAD saves EDI/ESI/EBP/original ESP/EBX/EDX/ECX/EAX; the following DWORD holds original EFLAGS.
            Hex("9C60FC");
            Store(record + 4, site.Call);
            Store(record + 8, site.Text);
            Hex("64A124000000A3"); Word(record + 12); // The thread ID from the current x86 TEB.
            Hex("8BF4BF"); Word(record + 32); Hex("B909000000F3A5");
            // All four MessageBoxW arguments were pushed before the original call; do not assume readable stack data beyond them.
            Hex("8B74240C83C604BF"); Word(record + 80); Hex("B904000000F3A5");
            Store(record, 1);
            // This path is definitively fatal. Preserve the scene without displaying a dialog, executing unknown post-exit code, or running game logic. Host polling detects it and terminates the entire Job.
            Hex("F390EBFC");
            Install(trampoline, code.ToArray());

            byte[] jump = new byte[6];

            jump[0] = 0xE9;
            BinaryPrimitives.WriteUInt32LittleEndian(jump.AsSpan(1), unchecked(trampoline - site.Call - 5));
            jump[5] = 0x90;
            Install(site.Call, jump);
        }
    }

    internal void ThrowIfFaulted()
    {
        for (int index = 0; index < Sites.Length; index++)
        {
            uint record = memory + (uint)index * 256;
            byte[] data = process.ReadMemory(record, 96);

            if (BinaryPrimitives.ReadUInt32LittleEndian(data) == 0) continue;

            uint threadId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            string registers = Convert.ToHexString(data.AsSpan(32, 36));
            string arguments = Convert.ToHexString(data.AsSpan(80, 16));

            throw new InvalidDataException($"AFDEV_NATIVE_FATAL branch={Sites[index].Branch}; site=0x{Sites[index].Call:X8}; " +
                $"text=0x{Sites[index].Text:X8}; thread={threadId}; message={MessagePrefix}; " +
                $"registers_edi_esi_ebp_esp_ebx_edx_ecx_eax_eflags_le={registers}; messagebox_args_le={arguments}。");
        }
    }

    internal void VerifyHooks()
    {
        foreach (var hook in hooks)
            AfdevNativeBootstrap.Require(process.ReadMemory(hook.Address, hook.Code.Length).AsSpan().SequenceEqual(hook.Code),
                $"原生资源错误保护被修改：0x{hook.Address:X8}。");
    }

    private void Install(uint address, byte[] code)
    {
        process.WriteMemory(address, code);
        AfdevNativeBootstrap.Require(process.ReadMemory(address, code.Length).AsSpan().SequenceEqual(code),
            $"原生资源错误保护写入验证失败：0x{address:X8}。");
        hooks.Add((address, code));
    }
}
