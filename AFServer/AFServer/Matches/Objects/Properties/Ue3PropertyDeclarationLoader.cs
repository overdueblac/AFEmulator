using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// Rereads property/structure declarations from dedicated research data without reading client files or
/// restoring native class instances.
/// </summary>
public static class Ue3PropertyDeclarationLoader
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Validates both out-of-band file fingerprints and complete serial fingerprints from the original
    /// identity catalog before returning the full catalog.
    /// </summary>
    /// <param name="path">Absolute path to declaration-only JSON.</param>
    /// <param name="expectedSha256">File SHA256 supplied by a trusted caller.</param>
    /// <param name="identities">Trusted identity catalog with explicit physical-package selection.</param>
    public static Ue3PropertyDeclarationCatalog LoadResearchSelection(string path, string expectedSha256,
        Ue3ObjectIdentityCatalog identities)
    {
        ArgumentNullException.ThrowIfNull(identities);

        try
        {
            Require(Path.IsPathFullyQualified(path), "声明证据必须使用绝对路径。");
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                && (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) == 0,
                "声明文件及所在目录不能为重解析点。");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            Require(stream.Length is > 0 and <= MaximumFileBytes, "声明文件为空或超过预算。");

            byte[] bytes = new byte[checked((int)stream.Length)];

            stream.ReadExactly(bytes);
            Require(Hash(bytes) == expectedSha256, "声明文件与带外信任锚不符。");

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });

            NoDuplicateKeys(document.RootElement);

            var root = document.RootElement;

            Require(root.GetProperty("schema_version").GetInt32() == 1 && root.GetProperty("candidate_only").GetBoolean()
                && !root.GetProperty("runtime_defaults_complete").GetBoolean()
                && root.GetProperty("verified_binary_sha256").GetString() == Ue3ObjectIdentityLoader.VerifiedBinarySha256,
                "声明版本或研究边界不符。");

            var selected = identities.CaptureSelection().ToDictionary(x => x.PhysicalPackage, StringComparer.Ordinal);
            var rows = root.GetProperty("objects");

            Require(rows.GetArrayLength() is > 0 and <= 4096, "声明对象数量超限。");

            List<Ue3PropertyDeclaration> properties = [];
            List<Ue3ScriptStructDeclaration> structs = [];
            HashSet<long> seen = [];

            foreach (var row in rows.EnumerateArray())
            {
                Require(selected.TryGetValue(row.GetProperty("physical_package").GetString()!, out var selection),
                    "声明不属于选定物理包。");

                var identity = identities.GetExport(selection!.PackageId, row.GetProperty("export_index").GetInt32());

                Require(seen.Add(identity.ObjectId), "声明物理身份重复。");

                var source = identities.GetExportSource(identity.PackageId, identity.ExportIndex);

                Require(source.SerialSha256 is not null && source.Bytes is > 0 and <= 1024 * 1024
                    && row.GetProperty("package_sha256").GetString() == source.PackageSha256
                    && row.GetProperty("logical_offset").GetInt32() == source.LogicalOffset
                    && row.GetProperty("bytes").GetInt32() == source.Bytes, "声明没有可信完整 serial 来源或范围不符。");

                byte[] serial = Convert.FromBase64String(row.GetProperty("serial_base64").GetString()!);

                Require(serial.Length == source.Bytes && Hash(serial) == source.SerialSha256
                    && row.GetProperty("serial_sha256").GetString() == source.SerialSha256, "声明原始 serial 指纹不符。");

                var type = identities.InspectReference(identity.PackageId, identity.ClassReference).LogicalPath;

                Require(type.Length == 2 && type[0] == new Ue3ObjectName("Core", 0) && type[1].Number == 0,
                    "声明必须具有真实 Core 类型引用。");

                var reader = new Reader(serial, identities, identity.PackageId);

                Require(reader.Int32() == identity.SerializedNetIndex && reader.Name() == new Ue3ObjectName("None", 0),
                    "声明 UObject prefix 不符。");

                int next = reader.Reference();

                if (type[1].Text == "ScriptStruct") structs.Add(ReadStruct(reader, identity, source));
                else properties.Add(ReadProperty(reader, identity, source, type[1].Text, next));
            }

            return new(identities, properties, structs);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or OverflowException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("属性声明专用数据格式无效。", error);
        }
    }

    private static Ue3PropertyDeclaration ReadProperty(Reader reader, Ue3ExportIdentity identity,
        Ue3ObjectSourceLocation source, string kind, int next)
    {
        int dimension = reader.Int32();

        Require(dimension is > 0 and <= 65536, "属性 ArrayDim 超出 profile。");

        ulong flags = reader.UInt64();

        _ = reader.Name();
        _ = reader.Reference();
        if ((flags & 0x20) != 0) _ = reader.Take(2);

        string[] references = kind switch
        {
            "IntProperty" or "FloatProperty" or "BoolProperty" or "NameProperty" or "StrProperty" => [],
            "ObjectProperty" or "ComponentProperty" => ["class"],
            "ClassProperty" => ["class", "meta_class"],
            "ByteProperty" => ["enum"],
            "StructProperty" => ["struct"],
            "ArrayProperty" => ["inner"],
            _ => throw new InvalidDataException("此属性声明 serializer 尚未纳入支持范围。")
        };
        var types = references.ToImmutableDictionary(x => x, _ => reader.Reference(), StringComparer.Ordinal);

        reader.End();
        return new(identity, source, kind, next, dimension, flags, types);
    }

    private static Ue3ScriptStructDeclaration ReadStruct(Reader reader, Ue3ExportIdentity identity,
        Ue3ObjectSourceLocation source)
    {
        int super = reader.Reference();

        Require(super == identity.SuperReference, "结构 UStruct.Super 与 export 目录不符。");
        _ = reader.Reference();

        int children = reader.Reference();

        _ = reader.Reference();
        _ = reader.Int32();
        _ = reader.Int32();

        int memoryBytes = reader.Int32(), storageBytes = reader.Int32();

        Require(memoryBytes >= 0 && storageBytes >= 0, "结构脚本长度为负。");
        _ = reader.Take(storageBytes);

        int flagsOffset = checked(source.LogicalOffset + reader.Position);
        uint flags = unchecked((uint)reader.Int32());
        int defaultsOffset = checked(source.LogicalOffset + reader.Position);
        byte[] defaults = reader.Take(source.Bytes - reader.Position);

        Require(defaults.Length >= 8, "结构默认值区缺少最小终止 tag 空间。");
        reader.End();
        return new(identity, source, super, children, flags, flagsOffset, defaultsOffset, defaults.Length, Hash(defaults));
    }

    private sealed class Reader(byte[] data, Ue3ObjectIdentityCatalog identities, long packageId)
    {
        internal int Position { get; private set; }

        internal byte[] Take(int count)
        {
            Require(count >= 0 && count <= data.Length - Position, "声明 serial 被截断。");

            byte[] value = data.AsSpan(Position, count).ToArray();

            Position += count;
            return value;
        }

        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        internal Ue3ObjectName Name() => identities.ResolvePackageName(packageId, Int32(), Int32());

        internal int Reference()
        {
            int value = Int32();

            _ = identities.InspectReference(packageId, value);
            return value;
        }

        internal void End() => Require(Position == data.Length, "声明 serial 未精确消费。");
    }

    private static void NoDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "声明 JSON 含重复字段。");
                NoDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) NoDuplicateKeys(item);
        }
    }

    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
