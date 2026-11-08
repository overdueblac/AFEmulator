using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Read-only validation of the PH v1.0.0.24 AFDEV PE layout, native anchors, and installed ServerMove v4.
/// </summary>
/// <remarks>
/// Based on the local reference Develop/af-emulator-main-New/tools/patches/tgame_servermove_v4.py:18–246.
/// Never installs patches, runs scripts, or starts processes. Static validation does not establish map,
/// server-mode, or session readiness. The outer package manifest pins the complete file version; this check
/// retains the reference implementation's supported date-fix variants.
/// </remarks>
public static class AfdevBinaryInspector
{
    private const int MaximumExecutableBytes = 256 * 1024 * 1024;
    private const uint ImageBase = 0x00400000;
    private const uint ServerMoveStub = 0x013A88D0;
    private const int ServerMoveLength = 639;
    private const string TemplateSha256 = "da3cabb501ac8d4eb937aba3759864e029c63af97d2a5964b26e72685c9429c4";
    private static readonly byte[] StockStub = [0xC2, 0x28, 0x00, 0xCC, 0xCC];

    // The complete restoration body at VA 0x025BE000 is verified; only six external CALL rel32 values are recalculated for the actual section address.
    private static readonly byte[] ServerMoveTemplate = Convert.FromBase64String(
        "VYvsU1ZXg+wwi/GLntgBAACF2w+EVgIAAPMPEEUI8w8QjvgDAAAPL8gPg0ACAACLhpADAAA7ww+EQgAAAA9XwPMPEUXw8w8RRezzDxFF6Ild0GoAjUXQUGoA/zU8PwYC/zU4PwYCi87ok8Dy/VCLBouAIAEAAIvO/9DpNgAAAPMPEEUM8w9ZBXviWwLzDxFF8PMPEEUQ8w9ZBXviWwLzDxFF7PMPEEUU8w9ZBXviWwLzDxFF6PMPEEUI8w9chvgDAADzD12GiAMAAPMPEUXk/3Xki87ohw8z/oXAD4UNAAAAD1fA8w8RReTpEgAAAPMPEEXk8w9Zg5AAAADzDxFF5ItFCImG+AMAAIsN+GsGAoXJD4Q+AAAA6GRYfv7ZXeCLReCJhgAEAACLRfAl////f4tN7IHh////fwvBi03ogeH///9/C8EPhAkAAACLReCJhhAEAACLRSyL0CX//wAAweoQagBSUIvO6HOhYv6LhpADAAA7ww+F9AAAAIuGYAAAAA+2i5QAAACD+QEPhBEAAACD+QIPhAgAAACJRdzpBQAAADPAiUXci4ZkAAAAiUXYi0UoJf8AAADB4AiJRdT/deT/ddT/ddj/ddyLy+j/KAL/8w8QReQPV9IPL8IPhmMAAACLDfhrBgKFyQ+EVQAAAGoA6Knfff6FwA+ERgAAAIO4rAMAAAAPhTkAAACLlmgAAAArVdRSi45kAAAAK03YUYuGYAAAACtF3FD/dej/dez/dfD/dST/deSLBou40AQAAIvO/9eLPou/zAQAAP91LP91KP91JP91IP91HP91GP91FP91EP91DP91CIvO/9eDxDBfXluL5V3CKADNzMw9");
    private static readonly (int Offset, uint Target)[] ExternalCalls =
    [
        (0x068, 0x004EA100), (0x0D4, 0x008EF060), (0x117, 0x00DA3980),
        (0x168, 0x00BE82E0), (0x1CC, 0x015E0AD0), (0x1F2, 0x00D9C1A0)
    ];
    private static readonly (uint Address, string Hex)[] NativeAnchors =
    [
        (0x008EF060, "83EC14833DA4E905"), (0x00DA3980, "6A00"),
        (0x00D9C1A0, "6AFF68"), (0x015E0AD0, "83EC108B4424"),
        (0x00934DA0, "83EC0C56"), (0x00BE82E0, "83EC5C"),
        (0x004EA100, "6AFF6810CE8B01"), (0x009564ED, "890D383F060289153C3F0602")
    ];
    private static readonly (string Name, uint Address, uint Move, uint Walking)[] ControllerTables =
    [
        ("TGPlayerController", 0x01D64720, 0x008F24B0, ServerMoveStub),
        ("TGPVPPlayerController", 0x01D651D8, 0x008F24B0, ServerMoveStub),
        ("TGTeamPlayerController", 0x01D65728, 0x008F24B0, ServerMoveStub),
        ("PVEPlayerController", 0x01E24C28, 0x008F24B0, ServerMoveStub),
        ("TGMechaPlayerController", 0x01E25180, 0x008F24B0, ServerMoveStub),
        ("TGAIGamePlayerController", 0x01E256D8, 0x008F24B0, ServerMoveStub),
        ("PZPlayerControllerBase", 0x01E25C28, 0x0162AA20, 0x0162ABB0),
        ("TGBioPlayerController", 0x01E42FE0, 0x008F24B0, ServerMoveStub)
    ];

    /// <summary>
    /// Reads a stable file snapshot and validates the patch. Missing, unpatched, or unknown builds fail
    /// without modifying the file.
    /// </summary>
    /// <param name="executablePath">
    /// The absolute AFDEV path resolved by the caller against the executable directory.
    /// </param>
    /// <returns>Hashes from the same validated file contents.</returns>
    /// <exception cref="InvalidDataException">
    /// The PE layout, native anchors, or ServerMove v4 contents do not match.
    /// </exception>
    public static AfdevBinaryIdentity Inspect(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("AFDEV 二进制校验需要绝对路径。", nameof(executablePath));
        }

        // Exclude other write handles during validation; hashing and byte checks use the same snapshot.
        using var input = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Require(input.Length is >= 1024 and <= MaximumExecutableBytes, "AFDEV 文件长度不在允许范围内。");

        byte[] bytes = new byte[checked((int)input.Length)];

        input.ReadExactly(bytes);

        var image = new PeImage(bytes);

        ValidateNativeBuild(image, bytes);

        byte[] serverMove = ValidateServerMove(image);

        return new AfdevBinaryIdentity(Hash(bytes), Hash(serverMove));
    }

    private static void ValidateNativeBuild(PeImage image, ReadOnlySpan<byte> bytes)
    {
        foreach (var anchor in NativeAnchors)
        {
            byte[] expected = Convert.FromHexString(anchor.Hex);

            Require(image.AtAddress(anchor.Address, expected.Length).SequenceEqual(expected),
                $"AFDEV 原生锚点不匹配：0x{anchor.Address:X8}。");
        }

        ValidateBranch(image, 0x00A7C918, 0xE8, 0x00D9C1A0, "WorldInfo 调用");
        Require(image.AtAddress(0x00A7C91D, 7).SequenceEqual(Convert.FromHexString("83B8AC03000000")),
            "AFDEV WorldInfo.Pauser +0x3AC 证据不匹配。");
        ValidateBranch(image, 0x00934DFD, 0xE8, 0x00BE82E0, "SetRotation 调用");
        ValidateBranch(image, 0x0162ACC4, 0xE8, ServerMoveStub, "PZ 基类调用");

        foreach (var table in ControllerTables)
        {
            Require(image.AddressUInt32(table.Address + 0x4C8) == ServerMoveStub
                && image.AddressUInt32(table.Address + 0x4CC) == 0x008F2620
                && image.AddressUInt32(table.Address + 0x4D0) == table.Move
                && image.AddressUInt32(table.Address + 0x528) == table.Walking,
                $"AFDEV {table.Name} 移动虚表槽不匹配。");
        }

        // The reference patcher counts nonoverlapping literal matches across the whole file; do not limit the check to a few known vtables.
        ReadOnlySpan<byte> reference = [0xD0, 0x88, 0x3A, 0x01];
        int count = 0;
        int position;

        while ((position = bytes.IndexOf(reference)) >= 0)
        {
            count++;
            bytes = bytes[(position + reference.Length)..];
        }

        Require(count == 33, $"AFDEV ServerMove 字面引用数不匹配：{count}，预期 33。");
    }

    private static byte[] ValidateServerMove(PeImage image)
    {
        PeSection[] sections = image.Sections.Where(section => section.Name == ".afm4").ToArray();

        if (sections.Length == 0 && image.AtAddress(ServerMoveStub, 5).SequenceEqual(StockStub))
        {
            throw new InvalidDataException("AFDEV 尚未安装必需的 ServerMove v4/.afm4；请提供已准备的运行包。");
        }

        Require(sections.Length == 1, "AFDEV 必须包含唯一的 .afm4 ServerMove v4 节。");

        PeSection section = sections[0];
        uint codeAddress = checked(ImageBase + section.Rva);

        // The verified installer creates this section; accept only RX code and the complete 639-byte function body.
        Require(section.VirtualSize == ServerMoveLength && section.Characteristics == 0x60000020
            && section.RawSize == Align(ServerMoveLength, image.FileAlignment),
            "AFDEV .afm4 节长度或执行属性不匹配。");
        ValidateBranch(image, ServerMoveStub, 0xE9, codeAddress, "ServerMove v4 跳转");
        Require(ServerMoveTemplate.Length == ServerMoveLength && Hash(ServerMoveTemplate) == TemplateSha256,
            "内置 ServerMove v4 校验模板损坏。");

        byte[] expected = (byte[])ServerMoveTemplate.Clone();

        foreach (var call in ExternalCalls)
        {
            long displacement = (long)call.Target - (codeAddress + (long)call.Offset + 5);

            Require(expected[call.Offset] == 0xE8 && displacement is >= int.MinValue and <= int.MaxValue,
                "ServerMove v4 外部调用无法按已验证格式重定位。");
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(call.Offset + 1, 4), (int)displacement);
        }

        ReadOnlySpan<byte> actual = image.AtAddress(codeAddress, ServerMoveLength);

        Require(actual.SequenceEqual(expected), "AFDEV .afm4 完整函数体不符合已验证的 ServerMove v4。");

        return actual.ToArray();
    }

    private static void ValidateBranch(PeImage image, uint address, byte opcode, uint target, string description)
    {
        ReadOnlySpan<byte> code = image.AtAddress(address, 5);
        long actualTarget = (long)address + 5 + BinaryPrimitives.ReadInt32LittleEndian(code[1..]);

        Require(code[0] == opcode && actualTarget == target, $"AFDEV {description}不匹配：0x{address:X8}。");
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static uint Align(uint value, uint alignment) => checked((value + alignment - 1) & ~(alignment - 1));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record PeSection(string Name, uint VirtualSize, uint Rva, uint RawSize, uint Raw, uint Characteristics);

    /// <summary>
    /// A bounded, read-only PE32 view rejecting address ambiguity from overlapping sections, virtual
    /// zero-fill, or relocations.
    /// </summary>
    private sealed class PeImage
    {
        private readonly byte[] data;

        public PeImage(byte[] data)
        {
            this.data = data;

            Require(Slice(0, 2).SequenceEqual("MZ"u8), "AFDEV 缺少 MZ 签名。");

            long pe = UInt32(0x3C);

            Require(pe >= 0x40 && Slice(pe, 4).SequenceEqual("PE\0\0"u8), "AFDEV 缺少有效 PE 签名。");

            long coff = pe + 4;
            int count = UInt16(coff + 2);
            int optionalSize = UInt16(coff + 16);
            ushort characteristics = UInt16(coff + 18);
            long optional = coff + 20;

            Require(UInt16(coff) == 0x014C && count is > 0 and <= 96 && optionalSize == 224
                && UInt16(optional) == 0x010B && (characteristics & 0x2002) == 0x0002,
                "AFDEV 必须是有效的 x86 PE32 可执行文件。");
            Require(UInt32(optional + 28) == ImageBase && (UInt16(optional + 70) & 0x0040) == 0,
                "AFDEV ImageBase 或动态重定位属性不匹配。");

            uint sectionAlignment = UInt32(optional + 32);
            uint imageSize = UInt32(optional + 56);
            uint headerSize = UInt32(optional + 60);
            long sectionTable = optional + optionalSize;

            FileAlignment = UInt32(optional + 36);

            Require(sectionAlignment == 4096 && FileAlignment == 512 && imageSize > 0
                && imageSize <= uint.MaxValue - ImageBase && imageSize % sectionAlignment == 0
                && headerSize > 0 && headerSize <= data.Length && headerSize % FileAlignment == 0
                && sectionTable + count * 40L <= headerSize,
                "AFDEV PE 对齐或头部边界不匹配。");
            Require(UInt32(optional + 92) == 16 && UInt32(optional + 136) == 0 && UInt32(optional + 140) == 0,
                "AFDEV 不允许包含重定位目录。");

            var sections = new List<PeSection>(count);

            for (int index = 0; index < count; index++)
            {
                long offset = sectionTable + index * 40L;
                ReadOnlySpan<byte> nameBytes = Slice(offset, 8);
                int terminator = nameBytes.IndexOf((byte)0);
                string name = Encoding.ASCII.GetString(terminator >= 0 ? nameBytes[..terminator] : nameBytes);
                var section = new PeSection(name, UInt32(offset + 8), UInt32(offset + 12),
                    UInt32(offset + 16), UInt32(offset + 20), UInt32(offset + 36));
                ulong span = Math.Max(section.VirtualSize, section.RawSize);

                Require(name.Length > 0 && name != ".reloc" && !sections.Any(previous => previous.Name == name),
                    "AFDEV 包含空名称、重复名称或重定位节。");
                Require(section.Rva >= headerSize && section.Rva % sectionAlignment == 0 && span > 0
                    && (ulong)section.Rva + span <= imageSize,
                    $"AFDEV {name} 虚拟地址越界。");
                Require(section.RawSize == 0 || (section.Raw >= headerSize && section.Raw % FileAlignment == 0
                    && section.RawSize % FileAlignment == 0 && (ulong)section.Raw + section.RawSize <= (ulong)data.Length),
                    $"AFDEV {name} 文件内容越界。");

                foreach (PeSection previous in sections)
                {
                    Require(!Overlaps(section.Rva, span, previous.Rva, Math.Max(previous.VirtualSize, previous.RawSize))
                        && !Overlaps(section.Raw, section.RawSize, previous.Raw, previous.RawSize),
                        "AFDEV PE 节存在重叠。");
                }

                sections.Add(section);
            }

            Sections = sections.ToArray();
        }

        public uint FileAlignment { get; }
        public PeSection[] Sections { get; }

        public uint AddressUInt32(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(AtAddress(address, 4));

        public ReadOnlySpan<byte> AtAddress(uint address, int length)
        {
            Require(address >= ImageBase, "AFDEV 地址低于 ImageBase。");

            uint rva = address - ImageBase;

            foreach (PeSection section in Sections)
            {
                if (rva < section.Rva || (ulong)rva >= (ulong)section.Rva + Math.Max(section.VirtualSize, section.RawSize))
                {
                    continue;
                }

                long delta = (long)rva - section.Rva;

                Require(length >= 0 && delta + length <= section.RawSize,
                    $"AFDEV 地址不是完整的磁盘内容：0x{address:X8}。");

                return Slice(section.Raw + delta, length);
            }

            throw new InvalidDataException($"AFDEV 地址未映射到 PE 节：0x{address:X8}。");
        }

        private ReadOnlySpan<byte> Slice(long offset, int length)
        {
            Require(offset >= 0 && length >= 0 && offset <= data.LongLength - length, "AFDEV PE 数据截断或偏移越界。");

            return data.AsSpan((int)offset, length);
        }

        private ushort UInt16(long offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
        private uint UInt32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));

        private static bool Overlaps(ulong start, ulong length, ulong otherStart, ulong otherLength) =>
            length != 0 && otherLength != 0 && start < otherStart + otherLength && otherStart < start + length;
    }
}
