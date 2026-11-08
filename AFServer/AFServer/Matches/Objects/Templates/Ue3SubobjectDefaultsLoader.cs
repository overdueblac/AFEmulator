using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// Rereads recovered ordinary-component prefixes/tags from dedicated research data. Runtime neither reads
/// client packages nor invokes development scripts. Accepts only ordinary templates beneath CDO Outer
/// chains, typed as Core.Component descendants, without state stacks.
/// </summary>
public static partial class Ue3SubobjectDefaultsLoader
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumSerialBytes = 1024 * 1024;
    private const ulong HasStack = 0x0200000000000000;

    /// <summary>
    /// Requires a trusted caller-pinned file fingerprint. Failed rereads return no partial set and leave
    /// existing identities unchanged.
    /// </summary>
    /// <param name="path">
    /// Absolute dedicated-JSON path containing only selected ordinary-template raw serials.
    /// </param>
    /// <param name="expectedSha256">Out-of-band file SHA256, not the file's own trust claim.</param>
    /// <param name="identities">Source-validated identities with explicit physical-package selection.</param>
    /// <param name="declarations">
    /// Optional same-catalog declarations. When supplied, binds fields strictly and decodes the three
    /// recovered structure profiles.
    /// </param>
    public static Ue3SubobjectDefaultsCatalog LoadResearchSelection(string path, string expectedSha256,
        Ue3ObjectIdentityCatalog identities, Ue3PropertyDeclarationCatalog? declarations = null)
    {
        ArgumentNullException.ThrowIfNull(identities);
        declarations?.RequireSameIdentities(identities);

        try
        {
            Require(Path.IsPathFullyQualified(path), "普通模板证据必须从显式绝对路径加载。");
            Require((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) == 0
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0,
                "普通模板数据文件及所在目录不能为重解析点。");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            Require(stream.Length is > 0 and <= MaximumFileBytes, "普通模板研究文件为空或超过预算。");

            byte[] bytes = new byte[checked((int)stream.Length)];

            stream.ReadExactly(bytes);
            Require(Hash(bytes) == expectedSha256, "普通模板研究文件与外部信任锚不符。");

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });

            NoDuplicateKeys(document.RootElement);

            var root = document.RootElement;

            Require(root.GetProperty("schema_version").GetInt32() == 1
                && root.GetProperty("candidate_only").GetBoolean()
                && !root.GetProperty("runtime_defaults_complete").GetBoolean()
                && root.GetProperty("verified_binary_sha256").GetString() == Ue3ObjectIdentityLoader.VerifiedBinarySha256,
                "普通模板 schema、原始版本或研究边界不符。");

            var selected = identities.CaptureSelection().ToDictionary(x => x.PhysicalPackage, StringComparer.Ordinal);
            var rows = root.GetProperty("objects");

            Require(rows.GetArrayLength() is > 0 and <= 1024, "普通模板对象数量超限。");

            List<Ue3SubobjectDefaultEvidence> records = [];
            HashSet<long> seen = [];

            foreach (var row in rows.EnumerateArray())
            {
                string package = row.GetProperty("physical_package").GetString()!;

                Require(selected.TryGetValue(package, out var selection), "普通模板物理包不在显式选择集合内。");

                var identity = identities.GetExport(selection!.PackageId, row.GetProperty("export_index").GetInt32());

                Require(seen.Add(identity.ObjectId), "普通模板物理身份重复。");
                records.Add(ReadObject(row, identities, identity, declarations));
            }

            return new(identities, records);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or OverflowException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("普通模板专用数据格式无效。", error);
        }
    }

    private static Ue3SubobjectDefaultEvidence ReadObject(JsonElement row, Ue3ObjectIdentityCatalog catalog,
        Ue3ExportIdentity identity, Ue3PropertyDeclarationCatalog? declarations)
    {
        var source = catalog.GetExportSource(identity.PackageId, identity.ExportIndex);

        Require(row.GetProperty("package_sha256").GetString() == source.PackageSha256
            && row.GetProperty("logical_offset").GetInt32() == source.LogicalOffset
            && row.GetProperty("bytes").GetInt32() == source.Bytes && source.Bytes <= MaximumSerialBytes,
            "普通模板与物理目录来源不符或 serial 超限。");
        Require((identity.ObjectFlags & (0x200 | HasStack)) == 0
            && !catalog.TryGetClass(identity.PackageId, identity.ExportIndex, out _),
            "只接受无 state stack 的普通模板，不接受 CDO 或类对象。");

        var type = catalog.ResolveObjectClass(identity.PackageId, identity.ExportIndex);
        var ancestry = catalog.ResolveClassAncestry(type.Definition.PackageId, type.Definition.ExportIndex);

        Require(ancestry.Any(x => x.Definition.LogicalPath.SequenceEqual(
            [new Ue3ObjectName("Core", 0), new Ue3ObjectName("Component", 0)])),
            "普通模板类型不是已确认的 Core.Component 后代。");

        var outer = identity;
        HashSet<long> outerIds = [];
        bool hasDefaultOuter = false;

        while (outer.OuterReference != 0)
        {
            Require(outerIds.Count < 128 && outerIds.Add(outer.ObjectId), "普通模板 outer 链循环或超限。");
            outer = catalog.ResolveReference(outer.PackageId, outer.OuterReference)!;
            hasDefaultOuter |= catalog.TryGetDefaultObjectClass(outer.PackageId, outer.ExportIndex, out _);
        }
        Require(hasDefaultOuter, "普通模板没有已核实的默认对象 outer 链。");

        byte[] serial = Convert.FromBase64String(row.GetProperty("serial_base64").GetString()!);
        string serialHash = Hash(serial);

        Require(serial.Length == source.Bytes && serialHash == row.GetProperty("serial_sha256").GetString()
            && (source.SerialSha256 is null || source.SerialSha256 == serialHash), "普通模板原始 serial 长度或指纹不符。");
        source = source with { SerialSha256 = serialHash };

        var reader = new SerialReader(serial, catalog, identity.PackageId);

        // 0x48B807..0x48B853: component reference, template FName beneath a CDO Outer, then UObject NetIndex.
        int owner = reader.Reference();
        var templateName = reader.Name();
        int netIndex = reader.Int32();

        Require(netIndex >= -1, "普通模板串行 NetIndex 非法。");

        var facts = ImmutableArray.CreateBuilder<Ue3ExplicitDefaultEvidence>();
        HashSet<(string, int, int)> tagNames = [];

        while (true)
        {
            int tagOffset = reader.Position;
            var name = reader.Name();

            if (name == new Ue3ObjectName("None", 0)) break;
            Require(facts.Count < 16384, "普通模板显式 tag 数量超限。");

            var kind = reader.Name();
            int count = reader.Int32(), arrayIndex = reader.Int32();

            Require(kind.Number == 0 && kind.Text.EndsWith("Property", StringComparison.Ordinal)
                && count >= 0 && arrayIndex is >= 0 and <= 65535
                && tagNames.Add((name.Text.ToUpperInvariant(), name.Number, arrayIndex)), "普通模板 tag 类型、长度或名称/槽重复。");

            Ue3ObjectName? extraName = null;
            byte boolByte = 0;

            if (kind.Text is "StructProperty" or "ByteProperty") extraName = reader.Name();
            else if (kind.Text == "BoolProperty") boolByte = reader.Take(1)[0];

            int payloadOffset = reader.Position;
            byte[] payload = reader.Take(count);
            var field = declarations?.BindClassProperty(type.Definition.PackageId, type.Definition.ExportIndex, name);

            if (field is not null) ValidateField(field, kind.Text, arrayIndex);

            var binding = field is null
                ? global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToElement(new ReadObjectJson1 { status = "unrecovered_declaration_binding" })
                : DescribeBinding(field, kind.Text);
            var value = kind.Text == "StructProperty" && field is not null
                ? DecodeStruct(payload, extraName!, field, declarations!, catalog, identity.PackageId,
                    checked(source.LogicalOffset + payloadOffset))
                : DecodeValue(kind.Text, payload, boolByte, extraName, catalog, identity.PackageId);

            facts.Add(new(name, arrayIndex, kind.Text, checked(source.LogicalOffset + tagOffset),
                checked(source.LogicalOffset + payloadOffset), count, Hash(payload), value, binding));
        }

        var tail = new Ue3SubobjectNativeTail(checked(source.LogicalOffset + reader.Position),
            serial.Length - reader.Position, Hash(serial.AsSpan(reader.Position)));

        return new(identity, source, owner, templateName, netIndex, facts.ToImmutable(), tail);
    }

    private static JsonElement DecodeStruct(byte[] payload, Ue3ObjectName tagStructName, Ue3PropertyDeclaration field,
        Ue3PropertyDeclarationCatalog declarations, Ue3ObjectIdentityCatalog catalog, long payloadPackageId,
        int payloadLogicalOffset)
    {
        var structure = declarations.ResolveStruct(field);

        Require(structure.Identity.LogicalPath[^1] == tagStructName, "结构 tag FName 与实际字段的 struct 引用不符。");

        string path = structure.Identity.DisplayPath;
        bool vector = path == "Core.Object.Vector";
        bool lighting = path == "Engine.LightComponent.LightingChannelContainer";
        bool collision = path == "Engine.PrimitiveComponent.RBCollisionChannelContainer";

        if (!vector && !lighting && !collision)
            return global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToElement(new DecodeStructJson1 { status = "unsupported",
                reason = "struct_serializer_profile_not_recovered", serialized_type_metadata = tagStructName });

        var fields = declarations.GetStructFields(structure);

        if (vector)
        {
            Require(structure.StructFlags == 0x30 && fields.Length == 3
                && fields.Select(x => x.Identity.LogicalPath[^1]).SequenceEqual(
                    [new Ue3ObjectName("X", 0), new Ue3ObjectName("Y", 0), new Ue3ObjectName("Z", 0)])
                && fields.All(x => x.Kind == "FloatProperty" && x.ArrayDimension == 1),
                "Vector 缺少已核实的 flags/children/scalar 声明链。");
            Require(payload.Length == 12, "Vector payload 不是三个精确 binary32 word。");

            var reader = new SerialReader(payload, catalog, payloadPackageId);
            Dictionary<string, float> values = [];
            List<object> components = [];

            foreach (var component in fields)
            {
                int start = reader.Position;
                byte[] bytes = reader.Take(4);
                int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                float value = BitConverter.Int32BitsToSingle(bits);

                Require(float.IsFinite(value), "Vector 包含非有限分量，不能作为已解码空间值。");

                string name = component.Identity.LogicalPath[^1].Text;

                values.Add(name, value);
                components.Add(new DecodeStructJson2 { name = name, value = value, bits_u32_hex = unchecked((uint)bits).ToString("x8"),
                    payload_logical_offset = checked(payloadLogicalOffset + start), payload_bytes = 4,
                    payload_sha256 = Hash(bytes), declaration = DescribeBinding(component, "FloatProperty") });
            }

            return global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToElement(new DecodeStructJson3 { status = "decoded", encoding = "three_binary32_le",
                struct_identity = structure.Identity, struct_source = structure.Source,
                struct_flags_u32_hex = structure.StructFlags.ToString("x8"), value = values, components = components });
        }

        // 0x504422/0x50442E/0x5044E9..0x5044FD: verified package-loading profiles serialize flags=0 structures through the same tag serializer.
        // Recover only these two actual all-Bool declaration chains. flags=0 or tag-parsability does not imply arbitrary structure support.
        Require(structure.StructFlags == 0 && fields.Length == (lighting ? 61 : 19)
            && fields.All(x => x.Kind == "BoolProperty" && x.ArrayDimension == 1),
            "通道结构不符合已核实的 flags/Bool 字段链。");

        var members = ImmutableArray.CreateBuilder<Ue3ExplicitDefaultEvidence>();
        HashSet<(string, int, int)> seen = [];
        var tagged = new SerialReader(payload, catalog, payloadPackageId);

        while (true)
        {
            int start = tagged.Position;
            var name = tagged.Name();

            if (name == new Ue3ObjectName("None", 0)) break;
            Require(members.Count < fields.Length, "通道结构显式成员超过真实声明数量。");

            var kind = tagged.Name();
            int bytes = tagged.Int32(), index = tagged.Int32();

            Require(kind == new Ue3ObjectName("BoolProperty", 0) && bytes == 0
                && seen.Add((name.Text.ToUpperInvariant(), name.Number, index)), "通道成员类型/长度错误或重复。");

            var matches = fields.Where(x => x.Identity.LogicalPath[^1] == name).ToArray();

            Require(matches.Length == 1, "通道成员不是实际结构声明中的唯一字段。");
            ValidateField(matches[0], kind.Text, index);

            byte boolByte = tagged.Take(1)[0];
            var value = DecodeValue(kind.Text, [], boolByte, null, catalog, payloadPackageId);

            members.Add(new(name, index, kind.Text, checked(payloadLogicalOffset + start),
                checked(payloadLogicalOffset + tagged.Position), 0, Hash([]), value, DescribeBinding(matches[0], kind.Text)));
        }

        Require(tagged.Position == payload.Length, "通道结构 None 后仍有未消费字节。");

        var absent = fields.Where(x => !members.Any(m => m.Name == x.Identity.LogicalPath[^1]))
            .Select(x => x.Identity.LogicalPath[^1]).ToImmutableArray();

        return global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToElement(new DecodeStructJson4 { status = "decoded", encoding = "explicit_bool_member_tags",
            struct_identity = structure.Identity, struct_source = structure.Source,
            struct_flags_u32_hex = structure.StructFlags.ToString("x8"), complete_runtime_value = false,
            members = members.ToImmutable(), missing_members_not_defaulted = absent });
    }

    private static void ValidateField(Ue3PropertyDeclaration field, string serializedType, int arrayIndex)
    {
        bool compatible = field.Kind == serializedType
            || serializedType == "ObjectProperty" && field.Kind is "ComponentProperty" or "ClassProperty";

        Require(compatible && arrayIndex >= 0 && arrayIndex < field.ArrayDimension,
            "tag 类型或 ArrayIndex 与真实属性声明不符。");
    }

    private static JsonElement DescribeBinding(Ue3PropertyDeclaration field, string serializedType)
        => global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToElement(new DescribeBindingJson1 { status = "unique_declaration",
            package_id = field.Identity.PackageId, package = field.Source.PhysicalPackage,
            export_index = field.Identity.ExportIndex, path = field.Identity.DisplayPath,
            declared_kind = field.Kind, array_dimension = field.ArrayDimension, source = field.Source,
            serialized_type_matches_declared_kind = serializedType == field.Kind });

    private static JsonElement DecodeValue(string kind, byte[] payload, byte boolByte, Ue3ObjectName? extraName,
        Ue3ObjectIdentityCatalog catalog, long packageId)
    {
        var reader = new SerialReader(payload, catalog, packageId);
        Dictionary<string, object?> value = new() { ["status"] = "decoded" };

        switch (kind)
        {
            case "ObjectProperty":
                value["encoding"] = "package_object_index_i32";
                value["object_index"] = reader.Reference();
                break;
            case "IntProperty":
                value["encoding"] = "i32_le";
                value["value"] = reader.Int32();
                break;
            case "FloatProperty":
                int bits = reader.Int32();
                float number = BitConverter.Int32BitsToSingle(bits);

                value["encoding"] = "ieee754_binary32_le";
                value["bits_u32_hex"] = unchecked((uint)bits).ToString("x8");
                if (float.IsFinite(number)) value["value"] = number;
                else value["status"] = "nonfinite_bits_only";
                break;
            case "BoolProperty":
                value["encoding"] = "tag_byte_nonzero";
                value["value"] = boolByte != 0;
                break;
            case "ByteProperty":
                bool rawByte = extraName == new Ue3ObjectName("None", 0);

                value["encoding"] = rawByte ? "u8" : "enum_member_fname";
                value["value"] = rawByte ? reader.Take(1)[0] : reader.Name();
                break;
            case "NameProperty":
                value["encoding"] = "package_fname_i32_pair";
                value["value"] = reader.Name();
                break;
            case "StrProperty":
                int length = reader.Int32();

                Require(length is >= -65536 and <= 65536, "普通模板 FString 长度超限。");

                int width = length < 0 ? 2 : 1;
                byte[] encoded = reader.Take(Math.Abs(length) * width);

                Require(length == 0 || encoded.AsSpan(encoded.Length - width).IndexOfAnyExcept((byte)0) < 0,
                    "普通模板 FString 缺少终止符。");
                value["encoding"] = length == 0 ? "empty_fstring" : length > 0 ? "byte_to_u16" : "utf16_le";
                value["value"] = length == 0 ? "" : (length > 0 ? Encoding.Latin1 : new UnicodeEncoding(false, false, true))
                    .GetString(encoded.AsSpan(0, encoded.Length - width));
                break;
            default:
                // Unrecovered values retain ranges/hashes. Never guess decoding from payload length, field names, or same-named structures.
                return global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToElement(new DecodeStructJson1 { status = "unsupported",
                    reason = "value_serializer_not_recovered_for_this_type", serialized_type_metadata = extraName });
        }

        Require(reader.Position == payload.Length, "普通模板标量 payload 未精确消费或 bool 非零长度。");
        return global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToElement(value);
    }

    private sealed class SerialReader(byte[] data, Ue3ObjectIdentityCatalog catalog, long packageId)
    {
        internal int Position { get; private set; }

        internal byte[] Take(int bytes)
        {
            Require(bytes >= 0 && bytes <= data.Length - Position, "普通模板 prefix/tag/payload 被截断。");

            byte[] result = data.AsSpan(Position, bytes).ToArray();

            Position += bytes;
            return result;
        }

        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        internal Ue3ObjectName Name() => catalog.ResolvePackageName(packageId, Int32(), Int32());

        internal int Reference()
        {
            int reference = Int32();

            _ = catalog.InspectReference(packageId, reference);
            return reference;
        }
    }

    private static void NoDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "普通模板 JSON 含重复字段。");
                NoDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) NoDuplicateKeys(item);
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
