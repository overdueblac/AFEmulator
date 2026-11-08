using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// FName members and raw enum-serial slots. Slots do not automatically define wire encoding or gameplay
/// weights.
/// </summary>
public sealed record Ue3EnumMember(Ue3ObjectName Name, int Ordinal);

/// <summary>
/// Enum declaration checked against out-of-band fingerprints, without native UEnum creation or network
/// registration.
/// </summary>
public sealed record Ue3EnumDeclaration(Ue3ExportIdentity Identity, Ue3ObjectSourceLocation Source,
    int SerializedNetIndex, int NextFieldReference, ImmutableArray<Ue3EnumMember> Members);

/// <summary>
/// Reads enum serials composed of UObject prefix, UField.Next, and FName arrays for this client version.
/// </summary>
public static class Ue3EnumDeclarationReader
{
    /// <summary>
    /// Reads members after validating complete serial and physical identity. If the source catalog lacks an
    /// enum hash, an independent out-of-band anchor is still required.
    /// </summary>
    /// <param name="serial">Complete logical serial read from the selected export.</param>
    /// <param name="identities">
    /// Physical identities validated against package/schema/defaults trust anchors.
    /// </param>
    /// <param name="packageId">Explicit caller-selected physical package.</param>
    /// <param name="exportIndex">Core.Enum export ordinal.</param>
    /// <param name="expectedSerialSha256">
    /// Trusted caller-pinned SHA256, never self-supplied by the input.
    /// </param>
    public static Ue3EnumDeclaration Read(ReadOnlySpan<byte> serial, Ue3ObjectIdentityCatalog identities,
        long packageId, int exportIndex, string expectedSerialSha256)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var identity = identities.GetExport(packageId, exportIndex);
        var source = identities.GetExportSource(packageId, exportIndex);
        string hash = Convert.ToHexStringLower(SHA256.HashData(serial));

        Require(serial.Length == source.Bytes && serial.Length is >= 28 and <= 2068
            && hash == expectedSerialSha256 && (source.SerialSha256 is null || source.SerialSha256 == hash),
            "枚举完整 serial 与范围或带外信任锚不符。");

        var type = identities.InspectReference(packageId, identity.ClassReference).LogicalPath;

        Require(type.SequenceEqual([new Ue3ObjectName("Core", 0), new Ue3ObjectName("Enum", 0)]),
            "目标 export 不是 Core.Enum 声明。");

        int netIndex = Word(serial, 0);

        Require((identity.SerializedNetIndex is null || identity.SerializedNetIndex == netIndex)
            && Name(serial, 4, identities, packageId) == new Ue3ObjectName("None", 0),
            "枚举 UObject 前缀与已知身份不符。");

        int next = Word(serial, 12);

        if (next != 0)
        {
            var sibling = identities.ResolveReference(packageId, next)!;

            Require(sibling.PackageId == packageId && sibling.OuterReference == identity.OuterReference
                && sibling.ObjectId != identity.ObjectId, "枚举 Next 不是同 owner 的其他字段。");
        }

        int count = Word(serial, 16);

        Require(count is > 0 and <= 256 && serial.Length == 20 + count * 8,
            "枚举成员数量越界、截断或存在残余数据。");

        var members = ImmutableArray.CreateBuilder<Ue3EnumMember>(count);
        HashSet<Ue3ObjectName> names = [];

        for (int i = 0; i < count; i++)
        {
            var name = Name(serial, 20 + i * 8, identities, packageId);

            Require(names.Add(name) && name != new Ue3ObjectName("None", 0), "枚举成员重复或为 None 终止名称。");
            members.Add(new(name, i));
        }

        return new(identity, source with { SerialSha256 = hash }, netIndex, next, members.MoveToImmutable());
    }

    private static int Word(ReadOnlySpan<byte> serial, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(serial.Slice(offset, 4));

    private static Ue3ObjectName Name(ReadOnlySpan<byte> serial, int offset,
        Ue3ObjectIdentityCatalog identities, long packageId) =>
        identities.ResolvePackageName(packageId, Word(serial, offset), Word(serial, offset + 4));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
