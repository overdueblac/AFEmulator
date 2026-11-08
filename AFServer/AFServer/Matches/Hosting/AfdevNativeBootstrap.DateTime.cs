namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevNativeBootstrap
{
    private const uint DateTimeEntryAddress = 0x014B9510;
    private const uint DateTimeTrampolineAddress = 0x02407810;
    private const int DateTimeEntryOffset = 0x010B8910;
    private const int DateTimeTrampolineOffset = 0x01E7D210;
    private const string AuditedAfdevImageSha256 = "14dbe9c8722b8e6433427aae689a4238cb1677a66174159bddb3b62e0fcb91bc";
    private static readonly byte[] DateTimeOriginal = Convert.FromHexString("83EC24538B5C242C");
    private static readonly byte[] DateTimePatchedEntry = new X86Code()
        .Jump(DateTimeEntryAddress, DateTimeTrampolineAddress).Hex("909090").ToArray();
    private static readonly byte[] DateTimeTrampoline = BuildAuditedDateTimeTrampoline();
    private bool verifiedDateTimePatch;

    /// <summary>
    /// Restores the audited static date patch only in the validation snapshot. The caller must still verify
    /// the original entry point through the code-section hash.
    /// </summary>
    /// <remarks>
    /// Based on tgame_binary.py build_static_trampoline and EDGE's audited date-fix constants. Reserved
    /// images differ only in eight entry bytes and 79 bytes of original .xdat1 zero-fill. Normalization
    /// must reproduce the complete audited image byte-for-byte. Its hash also constrains PE mapping,
    /// executable attributes, return position, and every other section; a newly observed hash alone never
    /// grants acceptance.
    /// </remarks>
    internal static bool NormalizeAuditedDateTimePatch(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Require(image.Length >= DateTimeEntryOffset + DateTimeOriginal.Length, "AFDEV 日期入口超出映像边界。");

        var entry = image.AsSpan(DateTimeEntryOffset, DateTimeOriginal.Length);

        if (entry.SequenceEqual(DateTimeOriginal))
            return false;

        Require(entry.SequenceEqual(DateTimePatchedEntry), "AFDEV 日期入口不属于已审计的原始或静态兼容版本。");
        Require(DateTimeTrampoline.Length == 79 && image.Length >= DateTimeTrampolineOffset + DateTimeTrampoline.Length,
            "AFDEV 日期兼容跳板超出已审计映像边界。");

        var trampoline = image.AsSpan(DateTimeTrampolineOffset, DateTimeTrampoline.Length);

        Require(trampoline.SequenceEqual(DateTimeTrampoline), "AFDEV 日期兼容跳板的参数、原指令或返回地址不匹配。");

        // Modify only the current managed snapshot. Disk and native processes keep the date fix; the restored complete image must match the existing audit baseline.
        DateTimeOriginal.CopyTo(entry);
        trampoline.Clear();
        Require(Hash(image) == AuditedAfdevImageSha256,
            "AFDEV 日期兼容映像还原后与已审计完整映像不一致，拒绝额外修改。");

        return true;
    }

    private static byte[] BuildAuditedDateTimeTrampoline()
    {
        var code = new X86Code().Hex("817C24046C0700007D38");
        uint[] values = [2026, 9, 19, 12, 0, 0, uint.MaxValue];

        // Correct all seven arguments only when the year is below 1900. Otherwise, preserve the year.
        // Execute the complete overwritten prologue and return to the original entry +8.
        for (var index = 0; index < values.Length; index++)
            code.Hex("C74424").Byte(checked((byte)((index + 1) * 4))).UInt32(values[index]);

        return code.Bytes(DateTimeOriginal)
            .Jump(DateTimeTrampolineAddress, DateTimeEntryAddress + (uint)DateTimeOriginal.Length).ToArray();
    }

    private void VerifyDateTimeCode()
    {
        Verify(DateTimeEntryAddress, verifiedDateTimePatch ? DateTimePatchedEntry : DateTimeOriginal);

        if (verifiedDateTimePatch)
            Verify(DateTimeTrampolineAddress, DateTimeTrampoline);
    }
}
