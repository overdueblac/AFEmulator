using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// Explicit serialized host-zombie mappings. Retains FNames for enums without guessing runtime numbers or
/// creating objects.
/// </summary>
public sealed record Ue3BioHostClassEntry(Ue3ObjectName ZombieType, string ClassName,
    int LogicalOffset, int Bytes, string Sha256);

/// <summary>
/// Source evidence for two independently ordered arrays, without merging structure defaults or acting as
/// infection rules/factories.
/// </summary>
public sealed record Ue3BioHostClassDefaults(Ue3ObjectSourceLocation Source,
    ImmutableArray<Ue3BioHostClassEntry> PawnClasses, ImmutableArray<Ue3BioHostClassEntry> WeaponClasses);

/// <summary>Reads the HostZombiePawnClassMap array profile from the confirmed TGBioMatch CDO.</summary>
public static class Ue3BioHostClassDefaultsReader
{
    /// <summary>
    /// Returns explicit values after validating the full CDO fingerprint and all physical declarations.
    /// Rejects missing/unknown fields and trailing bytes.
    /// </summary>
    /// <param name="serial">
    /// Complete serial from a trusted CDO source, not an independently self-signed payload.
    /// </param>
    /// <param name="identities">
    /// Identity catalog with out-of-band package, schema, and defaults trust anchors.
    /// </param>
    /// <param name="declarations">
    /// Seven array, inner, member, and structure declarations extracted from the same catalog.
    /// </param>
    /// <param name="packageId">Explicitly selected TGBioGame.u physical package identity.</param>
    public static Ue3BioHostClassDefaults Read(ReadOnlySpan<byte> serial, Ue3ObjectIdentityCatalog identities,
        Ue3PropertyDeclarationCatalog declarations, long packageId)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(declarations);
        declarations.RequireSameIdentities(identities);

        var type = identities.GetClass(packageId, 2906);
        var source = identities.GetExportSource(packageId, type.DefaultObject.ExportIndex);

        Require(source.PhysicalPackage == "TGBioGame.u" && type.Definition.DisplayPath == "TGBioGame.TGBioMatch"
            && type.DefaultObject.ExportIndex == 2907 && serial.Length == source.Bytes
            && source.SerialSha256 == Hash(serial), "BIO CDO 完整来源或物理身份不符。");

        var pawn = ReadArray("HostZombiePawnClassName", serial, type, source, identities, declarations);
        var weapon = ReadArray("HostZombieWeaponClassName", serial, type, source, identities, declarations);

        return new(source, pawn, weapon);
    }

    private static ImmutableArray<Ue3BioHostClassEntry> ReadArray(string name, ReadOnlySpan<byte> serial,
        Ue3ClassIdentity type, Ue3ObjectSourceLocation source, Ue3ObjectIdentityCatalog identities,
        Ue3PropertyDeclarationCatalog declarations)
    {
        long packageId = type.Definition.PackageId;
        var field = declarations.BindClassProperty(packageId, type.Definition.ExportIndex, new(name, 0));

        Require(field.Kind == "ArrayProperty" && field.ArrayDimension == 1
            && field.TypeReferences.ContainsKey("inner"), "母体映射不是已恢复的标量 ArrayProperty 声明。");

        var innerIdentity = identities.ResolveReference(packageId, field.TypeReferences["inner"])
            ?? throw new InvalidDataException("母体数组缺少 inner 引用。");
        var inner = declarations.GetProperty(innerIdentity.PackageId, innerIdentity.ExportIndex);

        Require(inner.Identity.OuterReference == field.Identity.ExportIndex && inner.Identity.PackageId == packageId
            && inner.Kind == "StructProperty" && inner.ArrayDimension == 1, "母体数组 inner owner/type 不符。");

        var structure = declarations.ResolveStruct(inner);
        var fields = declarations.GetStructFields(structure);

        Require(structure.Identity.DisplayPath == "TGBioGame.TGBioMatch.HostZombiePawnClassMap"
            && structure.StructFlags == 1 && fields.Length == 2
            && fields[0].Identity.LogicalPath[^1] == new Ue3ObjectName("ZombieType", 0)
            && fields[0].Kind == "ByteProperty" && fields[0].ArrayDimension == 1
            && fields[1].Identity.LogicalPath[^1] == new Ue3ObjectName("HostZombiePawnClass", 0)
            && fields[1].Kind == "StrProperty" && fields[1].ArrayDimension == 1,
            "母体结构不符合已恢复的 flags/Children/Next profile。");

        var enumIdentity = identities.ResolveReference(fields[0].Identity.PackageId, fields[0].TypeReferences["enum"])
            ?? throw new InvalidDataException("母体类型缺少枚举声明引用。");

        Require(enumIdentity.DisplayPath == "TGBioGame.TGBioType.EZombieType", "母体枚举物理声明不符。");

        var tags = type.ExplicitDefaultTags.Where(t => t.GetProperty("name")[2].GetString() == name).ToArray();

        Require(tags.Length == 1, "母体数组缺少唯一 CDO tag。");

        var tag = tags[0];

        Require(tag.GetProperty("name")[1].GetInt32() == 0 && tag.GetProperty("array_index").GetInt32() == 0
            && tag.GetProperty("type")[1].GetInt32() == 0
            && tag.GetProperty("type")[2].GetString() == "ArrayProperty", "母体数组 tag 类型/槽位不符。");

        var location = tag.GetProperty("source");
        int offset = location.GetProperty("payload_relative_offset").GetInt32();
        int bytes = location.GetProperty("payload_bytes").GetInt32();

        Require(offset >= 24 && bytes >= 0 && offset <= serial.Length - bytes
            && location.GetProperty("tag_relative_offset").GetInt32() == offset - 24,
            "母体数组越过 CDO 范围或 tag 定位不符。");

        var header = new Reader(serial.Slice(offset - 24, 24).ToArray(), identities, packageId);

        Require(header.Name() == new Ue3ObjectName(name, 0)
            && header.Name() == new Ue3ObjectName("ArrayProperty", 0)
            && header.Int32() == bytes && header.Int32() == 0, "母体数组原始 tag 头与投影不符。");

        byte[] payload = serial.Slice(offset, bytes).ToArray();

        Require(Hash(payload) == location.GetProperty("payload_sha256").GetString(), "母体数组 payload 指纹不符。");
        return DecodeArray(payload, identities, packageId, enumIdentity, checked(source.LogicalOffset + offset));
    }

    private static ImmutableArray<Ue3BioHostClassEntry> DecodeArray(byte[] payload,
        Ue3ObjectIdentityCatalog identities, long packageId, Ue3ExportIdentity enumIdentity, int logicalOffset)
    {
        var reader = new Reader(payload, identities, packageId);
        int count = reader.Int32();

        Require(count is >= 0 and <= 256 && count <= (payload.Length - 4) / 8, "母体数组数量超出预算或最小范围。");

        var entries = ImmutableArray.CreateBuilder<Ue3BioHostClassEntry>();

        for (int i = 0; i < count; i++)
        {
            int start = reader.Position;
            Ue3ObjectName? zombie = null;
            string? className = null;

            while (true)
            {
                var member = reader.Name();

                if (member == new Ue3ObjectName("None", 0)) break;

                var kind = reader.Name();
                int size = reader.Int32(), index = reader.Int32();

                Require(index == 0, "母体成员 ArrayIndex 非零。");
                if (member == new Ue3ObjectName("ZombieType", 0))
                {
                    Require(zombie is null && kind == new Ue3ObjectName("ByteProperty", 0) && size == 8
                        && reader.Name() == enumIdentity.LogicalPath[^1], "母体枚举 tag 重复或类型/元数据不符。");
                    zombie = reader.Name();
                }
                else if (member == new Ue3ObjectName("HostZombiePawnClass", 0))
                {
                    Require(className is null && kind == new Ue3ObjectName("StrProperty", 0), "母体类名 tag 重复或类型不符。");
                    className = ReadString(reader.Take(size));
                }
                else throw new InvalidDataException("母体结构出现未恢复成员。");
            }

            Require(zombie is not null && className is not null, "母体结构缺少显式字段，不合成缺省值。");

            int extent = reader.Position - start;

            entries.Add(new(zombie!, className!, checked(logicalOffset + start), extent,
                Hash(payload.AsSpan(start, extent))));
        }

        Require(reader.Position == payload.Length, "母体数组 None 后存在多余数据或元素数量不符。");
        return entries.ToImmutable();
    }

    private static string ReadString(byte[] bytes)
    {
        Require(bytes.Length >= 4, "母体 FString 缺少长度。");

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);

        Require(count is >= -65536 and <= 65536, "母体 FString 超出预算。");
        if (count == 0)
        {
            Require(bytes.Length == 4, "空 FString 含残余字节。");
            return "";
        }

        int width = count > 0 ? 1 : 2;

        Require(bytes.Length == 4 + Math.Abs(count) * width && bytes[^1] == 0
            && (width == 1 || bytes[^2] == 0), "母体 FString 长度或终止符错误。");
        try
        {
            return (width == 1 ? Encoding.Latin1 : new UnicodeEncoding(false, false, true))
                .GetString(bytes, 4, bytes.Length - 4 - width);
        }
        catch (DecoderFallbackException error) { throw new InvalidDataException("母体 FString UTF16 非法。", error); }
    }

    private sealed class Reader(byte[] bytes, Ue3ObjectIdentityCatalog identities, long packageId)
    {
        internal int Position { get; private set; }

        internal byte[] Take(int count)
        {
            Require(count >= 0 && count <= bytes.Length - Position, "母体数组数据截断。");

            byte[] value = bytes.AsSpan(Position, count).ToArray();

            Position += count;
            return value;
        }

        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal Ue3ObjectName Name() => identities.ResolvePackageName(packageId, Int32(), Int32());
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
