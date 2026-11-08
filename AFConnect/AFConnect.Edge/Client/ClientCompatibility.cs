using System.Buffers.Binary;

namespace AFConnect.EDGE.Client;

/// <summary>
/// Defines player-client version boundaries and date compatibility patches without loading or starting
/// external TCLS components.
/// </summary>
public static class ClientCompatibility
{
    internal static readonly byte[] DateOriginal = Convert.FromHexString("83EC24538B5C242C");
    internal const uint DateRva = 0x10B9510;

    /// <summary>
    /// Checks the player executable and bundled shared-memory reader required for direct launch.
    /// </summary>
    public static string ValidateGameRoot(string gameRoot)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("启动器需要 Windows x64。");
        if (string.IsNullOrWhiteSpace(gameRoot)) throw new ArgumentException("请配置游戏资源目录。", nameof(gameRoot));

        var root = Path.GetFullPath(gameRoot);

        foreach (string relative in new[] { "Binaries/Win32/TGame.exe", "Binaries/Win32/TenProxy.dll" })
        {
            string path = Path.Combine(root, relative);

            if (!File.Exists(path)) throw new FileNotFoundException("游戏资源缺失：" + relative, path);
        }

        return root;
    }
    internal static bool PatchDateTime(NativeProcess.Handle process, ulong imageBase)
    {
        var target = checked(imageBase + DateRva);
        var entry = NativeProcess.Read(process, target, DateOriginal.Length);

        if (MatchesDateTimePatch(entry, checked((uint)target), (address, count) =>
            {
                if (!NativeProcess.IsExecutableMemory(process, address, count))
                    throw new InvalidOperationException("Datetime trampoline is not committed executable memory.");

                return NativeProcess.Read(process, address, count);
            }))
        {
            // The main thread is still suspended. Validate the entire trampoline, then recheck the entry point; do not reinstall a known patch.
            NativeProcess.RequireBytes(process, target, entry, "TGame existing datetime patch");
            return true;
        }

        NativeProcess.RequireBytes(process, target, DateOriginal, "TGame datetime");

        var trampoline = BuildDateTimeTrampoline(checked((uint)(target + (uint)DateOriginal.Length)));
        var allocation = NativeProcess.Allocate(process, trampoline.Length);
        var entryAttempted = false;

        try
        {
            NativeProcess.WriteExecutable(process, allocation, trampoline);

            var patch = new byte[] { 0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90 };
            // x86 EIP arithmetic wraps at 32 bits, matching the verified Python trampoline.
            var relative = unchecked((int)((uint)allocation - (uint)(target + 5)));

            BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1), relative);
            NativeProcess.WriteGuarded(process, target, DateOriginal, patch, "TGame datetime", () => entryAttempted = true);
        }
        finally
        {
            // Never free a trampoline after an entry write: a partial write may reference it.
            if (!entryAttempted) NativeProcess.Free(process, allocation);
        }

        return false;
    }

    /// <summary>
    /// Validates an existing relative static trampoline or absolute runtime trampoline; an entry-point JMP
    /// alone does not prove the patch is valid.
    /// </summary>
    /// <remarks>
    /// The bytes come from the verified tgame_binary.py reference. The complete argument fix, original
    /// instructions, and return target must match.
    /// </remarks>
    internal static bool MatchesDateTimePatch(ReadOnlySpan<byte> entry, uint target,
        Func<ulong, int, byte[]> readMemory)
    {
        if (entry.Length != DateOriginal.Length || entry[0] != 0xE9 ||
            !entry[5..].SequenceEqual(new byte[] { 0x90, 0x90, 0x90 })) return false;

        uint trampoline = unchecked(target + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(entry[1..5]));
        var absolute = BuildDateTimeTrampoline(checked(target + (uint)DateOriginal.Length));
        var relative = new byte[absolute.Length - 1];
        int prefixLength = absolute.Length - 6;

        absolute.AsSpan(0, prefixLength).CopyTo(relative);
        relative[prefixLength] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(relative.AsSpan(prefixLength + 1),
            unchecked((int)(target + (uint)DateOriginal.Length - (trampoline + (uint)relative.Length))));

        return Matches(relative) || Matches(absolute);

        bool Matches(byte[] expected)
        {
            if (trampoline < 0x10000 || (ulong)trampoline + (uint)expected.Length > 0x1_0000_0000UL) return false;

            try { return readMemory(trampoline, expected.Length).AsSpan().SequenceEqual(expected); }
            catch (Exception) { return false; }
        }
    }

    internal static byte[] BuildDateTimeTrampoline(uint continuation)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Convert.FromHexString("817C24046C0700007D38"));

        // Matches the verified Python fix: only a malformed year below 1900 is replaced.
        uint[] values = [2026, 9, 19, 12, 0, 0, uint.MaxValue];

        for (var i = 0; i < values.Length; i++)
        {
            writer.Write(new byte[] { 0xC7, 0x44, 0x24, checked((byte)((i + 1) * 4)) });
            writer.Write(values[i]);
        }
        writer.Write(DateOriginal);
        writer.Write((byte)0x68);
        writer.Write(continuation);
        writer.Write((byte)0xC3);
        return stream.ToArray();
    }
}
