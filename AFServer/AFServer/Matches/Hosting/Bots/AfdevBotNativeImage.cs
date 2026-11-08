using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Independently decoded fingerprints of complete native ranges. Deployment requires no research files.
/// </summary>
internal static class AfdevBotNativeImage
{
    private static readonly ConditionalWeakTable<AfdevOwnedProcess, VerifiedImage> VerifiedImages = new();
    private static readonly (uint Address, int Length, string Hash)[] Spans =
    [
        (0x00469FF0, 92, "a01fc6bbb7e60919e3b93f1934bb79ab384d2a295ebb3117d8b97cc4d1e5bac8"),
        (0x00483440, 1036, "b20cc5d1c13e100b2f0fcda061ecee68b502f2301da4fc54ab26eb3c42ca8743"),
        (0x0048DD60, 1012, "8af98cc072ed606da45e6dbe7094afcd910b52cc236066b0054557328715e247"),
        (0x0048EDD0, 52, "bc7e3eabe1082c0a0b8a47b1b5514777e8944c941cea05ce16b70e22b011aecf"),
        (0x0048EE10, 32, "cddfdec51be93ffeea620afa7d18ca1526706ed661f75fad1ae44d555e54eaab"),
        (0x004E9790, 1186, "2dfc4f16625ac931accea1399fc0060b9a1ec121b68c061a5adddd6c3719cec6"),
        (0x00501ED0, 27, "5aa3c0a0d469c408ab508a3b4ff4b6989ca1570150a1006632cb01b99517fa6a"),
        (0x00502470, 38, "07cef6e019a70f8d34a35356d5eeb77d3d09bc4022b290dc2b7af27bcbe91c26"),
        (0x005024A0, 53, "2e0ea20c889c464ff6a55e53ef5b044ca351e5c58fd75c507ca2e39eb18b617c"),
        (0x00502650, 38, "96c1ebd45d71ecdecc99ab698cc50897d7b4ac5cea9c62c59506110ece6775ef"),
        (0x00503BC0, 76, "220435f23c155c9ac6dc8a84b0306feed4af0130f18d08f809361dabaf92b467"),
        (0x005043E0, 298, "9fc4b4ccf66da23844e08ba273ef29c56093a1eb86612b12153e0a6bb1fc5267"),
        (0x00506970, 55, "e1fdb35c3355261281c1ffe38b60716935ac14f487c93fe8ab8d70ce18008c94"),
        (0x005069F0, 55, "7eef72668f325c8c0a330a3e95fdd6f386761c0672a52eb6e14b5f6ee26a2613"),
        (0x00506B70, 55, "025ec6dad7efbbcf2be0f2b758bc0e5858e74fb0a249e34d1e41d305531f0368"),
        (0x00507C30, 283, "8e01621a0c80e6fc35c0d5e4cdae546ac29171a2078d3ce0d30cce84620fd1b2"),
        (0x00509770, 4724, "86e56c2f5a848f0d1af93214db511b157eaaa774d75d3adee575f085a01a44b6"),
        (0x00519AE0, 3581, "0f261892ee294e0c4434e4cb638cf1eccb389e642b5dfbfde6c2caa0f9d2600e"),
        (0x00557000, 31, "56614e3ab50752e79339a6032f1b2ddc9a71c6293333174e640bf8188d6c4d58"),
        (0x006F9DE0, 32, "09c052f3c5cb18427a042c1558b181c98375bd07d7e6abec8e7051be5abee267"),
        (0x0075A9F0, 71, "d8503cf0d8557c28b4f26bc879f9c1aba8419b2eec823e59c204ec42c2127a0b"),
        (0x008EE3D0, 83, "08ae8c7d164f019c75d5c4eae1821221249390e0bebd439c4176ff415961aad9"),
        (0x008F2130, 149, "d795fe9d0d866e70e8967d6dac2d01b00f05491dea916cffe124e1af14b3f861"),
        (0x00952280, 51, "565cc7c24df8dc0a4230176836436f9de20547c2ac4ea8e9aeb47199af5797a4"),
        (0x009B9490, 6686, "cabcf2cbe6bae6da9dea8709a9d4ad63873e7b6d1e743bcb78961caaf3506e13"),
        (0x00A649E0, 4059, "b30e7c33260f70c23a4f222e1cc015870f9ad6a2f1739a42d2b6aadc565ab56c"),
        (0x00A8C040, 12491, "3a6607f75667ff943b359dd40925dc9a0bbdacb372d8011763a7b86deb5b4239"),
        (0x00BE9120, 597, "a50e0364283b9c06f94919b4e0a8cf9c4e5a7862ff1099f3bc8c6e7ce49bdb4f"),
        (0x00D9C1A0, 1244, "9e3cd97798c36420af9cc23756af9a662adae189877c1d7c36ef7d68faff381f"),
        (0x00DA38F0, 59, "28c9f17a50b28545f35d558aed417a04a1b9196e77811a22ebf8acec0ac0cb88"),
        (0x013AFF40, 51, "28c376b0cdd587797245242d922c4c03c1414ca0b2cf64169420839104ab3cd3"),
        (0x014A8970, 5501, "7b9b299e0d19fd9ab81c7670d319ab002a2eea926c57af94a50eb49e75828da1"),
        (0x019B7390, 80, "29543b6c9e3a67db3e6e1923537c66818c330e82a2ea32f30d2b1ca5e1a5b2d0"),
        (0x019CBCC0, 25, "1a98bd04121547222e2e17f430f5357f7e0e0877548617634c814b05e33d6133"),
    ];

    /// <summary>
    /// Validates the complete disk image and every call range in the suspended process. Successful identity
    /// belongs only to that same owned process object.
    /// </summary>
    public static string VerifySuspended(AfdevOwnedProcess process, string executable)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (VerifiedImages.TryGetValue(process, out _))
            throw new InvalidOperationException("同一 AFDEV 物理进程的 Bot 映像身份不能重复安装。");

        string actualHash = VerifyImageBytes(File.ReadAllBytes(executable));

        foreach (var span in Spans)
        {
            byte[] actual = process.ReadMemory(span.Address, span.Length);
            string hash = Convert.ToHexStringLower(SHA256.HashData(actual));

            if (hash != span.Hash)
                throw new InvalidDataException($"Bot 原生范围发生变化：0x{span.Address:X8}。");
        }

        VerifiedImages.Add(process, new(actualHash, process.ProcessId, process.CreationTimeUtc));

        return actualHash;
    }

    /// <summary>
    /// Normalizes the exact date patch in a separate managed snapshot before checking the full audit
    /// baseline, preserving input and actual disk fingerprints.
    /// </summary>
    internal static string VerifyImageBytes(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);

        string actualHash = TickHook.Hash(image);
        byte[] normalized = (byte[])image.Clone();

        AfdevNativeBootstrap.NormalizeAuditedDateTimePatch(normalized);

        if (TickHook.Hash(normalized) != TickHook.ImageHash)
            throw new InvalidDataException("Bot 原生调用仅支持完整审计映像及可精确还原到该映像的日期兼容版本。");

        return actualHash;
    }

    /// <summary>
    /// The actual image hash must come from full validation during this process's installation. A new hash
    /// or another process's success cannot authorize it.
    /// </summary>
    internal static bool IsVerifiedFor(AfdevOwnedProcess process, string imageSha256) =>
        process.IsAlive && VerifiedImages.TryGetValue(process, out VerifiedImage? verified) &&
        verified.ProcessId == process.ProcessId && verified.CreationTimeUtc == process.CreationTimeUtc &&
        string.Equals(verified.ImageSha256, imageSha256, StringComparison.Ordinal);

    private sealed record VerifiedImage(string ImageSha256, uint ProcessId, DateTimeOffset CreationTimeUtc);
}
