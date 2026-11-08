using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AFServer.AssaultFire.Matches.Objects;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// Bounded serialized-property results retaining array order, duplicates, and unknown native-structure
/// payloads.
/// </summary>
public sealed record NativeMapSequenceProperty(string Name, string Type, int ArrayIndex,
    NativeMapSourceRange PayloadSource, ImmutableArray<byte> Payload,
    int? Integer, float? Number, bool? Boolean, string? Text, Ue3ObjectName? NameValue, Ue3ObjectName? StructType,
    ImmutableArray<int> References, ImmutableArray<ImmutableArray<NativeMapSequenceProperty>> Items);

/// <summary>
/// Sequence objects cross-validated against original serials/map catalogs, without effective defaults or
/// runtime state.
/// </summary>
public sealed record NativeMapSequenceObject(NativeMapObject Object,
    ImmutableArray<NativeMapSequenceProperty> Properties)
{
    /// <summary>
    /// Gets a unique explicit property. Missing/same-named static-array elements cannot be silently
    /// substituted.
    /// </summary>
    public NativeMapSequenceProperty GetProperty(string name) =>
        Properties.Single(x => x.Name == name && x.ArrayIndex == 0);
}

/// <summary>
/// Original output-to-input slot connection, retaining self-loops/duplicates in order without implying an
/// impulse occurred.
/// </summary>
public sealed record NativeMapSequenceConnection(NativeMapObjectId Source, int OutputSlot,
    int LinkSlot, NativeMapObjectId Target, int InputSlot);

/// <summary>
/// Name candidates from Levels entries/WorldInfo arrays. Their slot numbers are independent; never writes
/// native Level caches.
/// </summary>
public sealed record NativeMapSequenceLevelName(int ActionSlot, int SerializedLevelReference,
    Ue3ObjectName LevelName, ImmutableArray<NativeMapStreamingSlot> Candidates);

/// <summary>
/// Reads explicit Kismet tagged arrays/interpolation-track structures from pinned incremental serials.
/// Supports observed array encodings only, retaining unknown structures raw without curve/world-transform
/// evaluation. Out-of-band evidence supplies names; top-level properties are cross-checked against existing
/// map catalogs.
/// </summary>
public sealed class NativeMapSequenceCatalog
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumItems = 4096;
    private readonly NativeMapInstanceCatalog map;
    private readonly ImmutableDictionary<NativeMapObjectId, NativeMapSequenceObject> objects;

    private NativeMapSequenceCatalog(string sha256, NativeMapInstanceCatalog map,
        ImmutableDictionary<NativeMapObjectId, NativeMapSequenceObject> objects,
        ImmutableArray<NativeMapSequenceConnection> connections)
    {
        EvidenceSha256 = sha256;
        this.map = map;
        this.objects = objects;
        Connections = connections;
    }

    /// <summary>Validated incremental-evidence SHA256.</summary>
    public string EvidenceSha256 { get; }

    /// <summary>Connections retained in evidence-object and original output/link-slot order.</summary>
    public ImmutableArray<NativeMapSequenceConnection> Connections { get; }

    /// <summary>Objects read, excluding map objects not requested.</summary>
    public int ObjectCount => objects.Count;

    /// <summary>Queries complete physical-package identity, never substituting display-name matches.</summary>
    public NativeMapSequenceObject GetObject(NativeMapObjectId id) => objects.TryGetValue(id, out var item)
        ? item : throw new InvalidDataException("序列对象没有对应的已验证增量 serial。");

    /// <summary>
    /// Finds raw-FName matches in a selected WorldInfo's explicit array, preserving actual indices from
    /// holes/duplicates. Original slots come only from privately constructed streaming catalogs;
    /// caller-copied/mutated WorldInfo evidence is rejected. Simulates no native first lookup, PIE
    /// renaming, cache writes, load requests, or Finished state.
    /// </summary>
    public ImmutableArray<NativeMapSequenceLevelName> FindLevelNames(NativeMapObjectId actionId,
        NativeMapStreamingCatalog streaming, NativeMapObjectId worldInfoId)
    {
        ArgumentNullException.ThrowIfNull(streaming);

        var action = GetObject(actionId);
        var world = streaming.Worlds.SingleOrDefault(x => x.WorldInfo.Id == worldInfoId)
            ?? throw new InvalidDataException("WorldInfo 不属于指定的已验证流式目录。");

        Require(action.Object.ClassPath == "Engine.SeqAct_MultiLevelStreaming"
            && world.WorldInfo.Id.PhysicalPackage == actionId.PhysicalPackage
            && world.WorldInfo.Id.PackageSha256 == actionId.PackageSha256
            && ReferenceEquals(map.GetObject(world.WorldInfo.Id), world.WorldInfo) && world.HasExplicitArray,
            "Levels 与 WorldInfo 必须属于同一已验证物理包，且具有显式流式数组。");

        var result = ImmutableArray.CreateBuilder<NativeMapSequenceLevelName>();

        foreach (var item in action.GetProperty("Levels").Items)
        {
            var level = Field(item, "Level", "ObjectProperty");
            var name = Field(item, "LevelName", "NameProperty");

            Require(item.Length == 2 && level.Integer.HasValue && name.NameValue is not null,
                "LevelStreamingNameCombo 缺少完整的显式 Level/LevelName。");

            var candidates = world.Slots.Where(slot => slot.Dependency is not null
                && slot.Dependency.StreamingObject.ExplicitProperties.TryGetValue("PackageName", out var packageName)
                && packageName.Type == "NameProperty"
                && packageName.PayloadSha256 == name.PayloadSource.Sha256).ToImmutableArray();

            result.Add(new(result.Count, level.Integer!.Value, name.NameValue!, candidates));
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Reads dedicated incremental evidence against a caller-pinned out-of-band fingerprint, never trusting
    /// the same file's own claims.
    /// </summary>
    public static NativeMapSequenceCatalog Load(string path, string expectedSha256, NativeMapInstanceCatalog map)
    {
        ArgumentNullException.ThrowIfNull(map);

        try
        {
            Require(Path.IsPathFullyQualified(path), "序列证据必须使用绝对路径。");
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                && (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) == 0,
                "序列证据路径不能为重解析点。");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            Require(stream.Length is > 0 and <= MaximumFileBytes, "序列证据文件大小超过预算。");

            byte[] bytes = new byte[checked((int)stream.Length)];

            stream.ReadExactly(bytes);
            Require(Hash(bytes) == expectedSha256, "序列证据与带外信任指纹不符。");

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });

            CheckKeys(document.RootElement);
            Require(document.RootElement.GetArrayLength() is > 0 and <= 256, "序列对象数量超过预算。");

            var objects = ImmutableDictionary.CreateBuilder<NativeMapObjectId, NativeMapSequenceObject>();
            var ordered = new List<NativeMapSequenceObject>();
            var namesByPackage = new Dictionary<string, string[]>(StringComparer.Ordinal);

            foreach (var row in document.RootElement.EnumerateArray())
            {
                var id = new NativeMapObjectId(row.GetProperty("package").GetString()!,
                    row.GetProperty("package_sha256").GetString()!, row.GetProperty("export_index").GetInt32());
                var item = map.GetObject(id);

                Require(item.HasDecodedProperties && item.ClassAncestryStatus == "resolved"
                    && item.ClassPath is "Engine.Sequence" or "Engine.SeqEvent_LevelLoaded"
                        or "Engine.SeqAct_MultiLevelStreaming" or "Engine.SeqAct_Interp"
                        or "Engine.SeqVar_Object" or "Engine.InterpData" or "Engine.InterpGroup"
                        or "Engine.InterpTrackMove",
                    "序列对象类或属性边界未恢复。");

                var source = row.GetProperty("source");
                byte[] serial = Convert.FromBase64String(row.GetProperty("serial_base64").GetString()!);

                Require(source.GetProperty("logical_offset").GetInt32() == item.Source.LogicalOffset
                    && source.GetProperty("bytes").GetInt32() == item.Source.Bytes
                    && source.GetProperty("sha256").GetString() == item.Source.Sha256
                    && serial.Length == item.Source.Bytes && serial.Length <= 1024 * 1024
                    && Hash(serial) == item.Source.Sha256,
                    "序列对象原始来源与地图目录不符。");

                string[] names = row.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToArray();

                Require(names.Length is > 0 and <= 100_000 && names.All(x => x is not null && x.Length <= 1024),
                    "序列名称表超过预算或包含无效名称。");
                if (namesByPackage.TryGetValue(id.PhysicalPackage, out var previous))
                    Require(names.SequenceEqual(previous), "同一物理包出现不一致的名称表。");
                else
                    namesByPackage.Add(id.PhysicalPackage, names);

                int start = row.GetProperty("tag_relative_offset").GetInt32();
                int first = item.ExplicitProperties.Values.Min(p => p.PayloadRelativeOffset
                    - (p.Type is "StructProperty" or "ByteProperty" ? 32 : p.Type == "BoolProperty" ? 25 : 24));

                Require(start == first && start >= 4, "序列 tag 起点与地图边界不符。");

                var parser = new Reader(serial, names, item.Source.LogicalOffset);
                var properties = parser.Tags(ref start, serial.Length, 0);

                Require(item.NativeTailSource is { } tail && start + item.Source.LogicalOffset == tail.LogicalOffset
                    && serial.Length - start == tail.Bytes && Hash(serial.AsSpan(start)) == tail.Sha256,
                    "序列 tag 终点或未解码 native tail 与原图不符。");
                Require(properties.Length == item.ExplicitProperties.Count, "顶层属性数量与地图目录不符。");
                foreach (var property in properties)
                {
                    Require(item.ExplicitProperties.TryGetValue(property.Name, out var original)
                        && original.Type == property.Type && original.ArrayIndex == property.ArrayIndex
                        && original.PayloadRelativeOffset + item.Source.LogicalOffset == property.PayloadSource.LogicalOffset
                        && original.PayloadBytes == property.Payload.Length
                        && original.PayloadSha256 == property.PayloadSource.Sha256,
                        "原始顶层 tag 与地图属性投影不符。");
                }

                var parsed = new NativeMapSequenceObject(item, properties);

                Require(objects.TryAdd(id, parsed), "增量序列对象重复。");
                ordered.Add(parsed);
            }

            var connections = ImmutableArray.CreateBuilder<NativeMapSequenceConnection>();

            foreach (var source in ordered)
            {
                var parent = source.Properties.SingleOrDefault(x => x.Name == "ParentSequence");

                if (parent is not null)
                {
                    Require(parent.Type == "ObjectProperty" && parent.Integer == source.Object.OuterReference
                        && objects.TryGetValue(source.Object.Id with { ExportIndex = parent.Integer.Value }, out var owner)
                        && owner.Object.ClassPath == "Engine.Sequence"
                        && owner.GetProperty("SequenceObjects").References.Contains(source.Object.Id.ExportIndex),
                        "显式 ParentSequence、outer 和 SequenceObjects 成员关系不一致。");
                }

                foreach (var array in source.Properties.Where(x => x.Name is "SequenceObjects" or "InterpGroups" or "InterpTracks"))
                foreach (int reference in array.References)
                {
                    if (reference == 0) continue;
                    Require(reference > 0, "外部序列成员的物理解析尚未恢复。");

                    var member = map.GetObject(source.Object.Id with { ExportIndex = reference });

                    Require(member.OuterReference == source.Object.Id.ExportIndex,
                        "序列或插值成员的 outer 不属于数组所在对象。");
                    Require(array.Name switch
                    {
                        "SequenceObjects" => source.Object.ClassPath == "Engine.Sequence"
                            && member.ClassAncestry.Contains("Engine.SequenceObject"),
                        "InterpGroups" => source.Object.ClassPath == "Engine.InterpData"
                            && member.ClassAncestry.Contains("Engine.InterpGroup"),
                        "InterpTracks" => source.Object.ClassPath == "Engine.InterpGroup"
                            && member.ClassAncestry.Contains("Engine.InterpTrack"),
                        _ => false
                    }, "数组属主或成员类与已支持的显式引用结构不符。");
                }

                var output = source.Properties.SingleOrDefault(x => x.Name == "OutputLinks");

                if (output is null) continue;
                for (int outputSlot = 0; outputSlot < output.Items.Length; outputSlot++)
                {
                    var links = Field(output.Items[outputSlot], "Links", "ArrayProperty").Items;

                    for (int linkSlot = 0; linkSlot < links.Length; linkSlot++)
                    {
                        int reference = Field(links[linkSlot], "LinkedOp", "ObjectProperty").Integer!.Value;
                        int input = Field(links[linkSlot], "InputLinkIdx", "IntProperty").Integer!.Value;

                        Require(reference > 0 && objects.TryGetValue(source.Object.Id with { ExportIndex = reference }, out _),
                            "输出连接目标缺少当前物理包的增量对象。");

                        var target = objects[source.Object.Id with { ExportIndex = reference }];

                        Require(source.Object.OuterReference == target.Object.OuterReference
                            && source.Object.ClassAncestry.Contains("Engine.SequenceOp")
                            && target.Object.ClassAncestry.Contains("Engine.SequenceOp")
                            && input >= 0 && input < target.GetProperty("InputLinks").Items.Length,
                            "输出连接跨越 Sequence 或目标 input 槽越界。");
                        connections.Add(new(source.Object.Id, outputSlot, linkSlot, target.Object.Id, input));
                    }
                }
            }

            return new(expectedSha256, map, objects.ToImmutable(), connections.ToImmutable());
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException
            or KeyNotFoundException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("序列增量证据的格式、边界或引用无效。", error);
        }
    }

    private static NativeMapSequenceProperty Field(ImmutableArray<NativeMapSequenceProperty> properties,
        string name, string type)
    {
        var result = properties.Single(x => x.Name == name && x.ArrayIndex == 0);

        Require(result.Type == type, "嵌套属性类型与已支持结构不符。");
        return result;
    }

    private sealed class Reader(byte[] bytes, string[] names, int logicalOffset)
    {
        private int totalTags;

        public ImmutableArray<NativeMapSequenceProperty> Tags(ref int position, int end, int depth)
        {
            Require(depth <= 12, "嵌套序列属性超过深度预算。");

            var result = ImmutableArray.CreateBuilder<NativeMapSequenceProperty>();
            var keys = new HashSet<(string, int)>();

            while (true)
            {
                var name = Name(ref position, end);

                if (name == new Ue3ObjectName("None", 0)) return result.ToImmutable();
                Require(name.Number == 0 && ++totalTags <= 16_384, "属性名称或总 tag 数量不受支持。");

                var type = Name(ref position, end);
                int size = Int(ref position, end), index = Int(ref position, end);

                Require(type.Number == 0 && size >= 0 && index >= 0 && keys.Add((name.Text, index)),
                    "属性类型、长度或重复 tag 无效。");

                bool? boolean = null;
                Ue3ObjectName? structure = null;

                if (type.Text == "BoolProperty")
                {
                    Require(position < end && size == 0 && bytes[position] <= 1, "Bool tag 编码无效。");
                    boolean = bytes[position++] != 0;
                }
                if (type.Text is "StructProperty" or "ByteProperty") structure = Name(ref position, end);
                Require(size <= end - position, "属性载荷截断。");

                int payloadStart = position, payloadEnd = checked(position + size);
                int? integer = null;
                float? number = null;
                string? text = null;
                Ue3ObjectName? nameValue = null;
                ImmutableArray<int> references = [];
                ImmutableArray<ImmutableArray<NativeMapSequenceProperty>> items = [];

                switch (type.Text)
                {
                    case "IntProperty":
                    case "ObjectProperty":
                        Require(size == 4, "整数或对象引用长度无效。");
                        integer = Int(ref position, payloadEnd);
                        break;
                    case "FloatProperty":
                        Require(size == 4, "浮点字段长度无效。");
                        number = BitConverter.Int32BitsToSingle(Int(ref position, payloadEnd));
                        Require(float.IsFinite(number.Value), "序列浮点字段不是有限值。");
                        break;
                    case "NameProperty":
                    case "ByteProperty" when size == 8:
                        Require(size == 8, "名称字段长度无效。");
                        nameValue = Name(ref position, payloadEnd);
                        break;
                    case "StrProperty":
                        int count = Int(ref position, payloadEnd);

                        Require(count > 0 && count <= 4096 && size == count + 4
                            && bytes[payloadEnd - 1] == 0, "当前仅支持完整零终止的单字节 FString。");
                        text = Encoding.Latin1.GetString(bytes, position, count - 1);
                        position = payloadEnd;
                        break;
                    case "ArrayProperty":
                        int length = Int(ref position, payloadEnd);

                        Require(length is >= 0 and <= MaximumItems, "数组元素数量超过预算。");
                        if (name.Text is "SequenceObjects" or "LinkedVariables" or "InterpGroups" or "InterpTracks")
                        {
                            Require(size == 4L + 4L * length, "对象引用数组长度不符。");

                            var refs = ImmutableArray.CreateBuilder<int>(length);

                            for (int i = 0; i < length; i++) refs.Add(Int(ref position, payloadEnd));
                            references = refs.MoveToImmutable();
                        }
                        else
                        {
                            Require(name.Text is "Levels" or "InputLinks" or "OutputLinks"
                                or "VariableLinks" or "Links" or "Points", "该数组的元素编码尚未恢复。");

                            var values = ImmutableArray.CreateBuilder<ImmutableArray<NativeMapSequenceProperty>>(length);

                            for (int i = 0; i < length; i++) values.Add(Tags(ref position, payloadEnd, depth + 1));
                            items = values.MoveToImmutable();
                        }
                        break;
                    case "StructProperty" when structure?.Text is "InterpCurveVector" or "InterpLookupTrack":
                        items = [Tags(ref position, payloadEnd, depth + 1)];
                        break;
                    default:
                        position = payloadEnd;
                        break;
                }

                Require(position == payloadEnd, "数组或结构存在未消费的尾部。");

                var payload = bytes.AsSpan(payloadStart, size);

                result.Add(new(name.Text, type.Text, index, new(logicalOffset + payloadStart, size, Hash(payload)),
                    [.. payload], integer, number, boolean, text, nameValue, structure, references, items));
            }
        }

        private int Int(ref int position, int end)
        {
            Require(position >= 0 && position <= end - 4, "序列整数字段截断。");

            int value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, 4));

            position += 4;
            return value;
        }

        private Ue3ObjectName Name(ref int position, int end)
        {
            int index = Int(ref position, end), number = Int(ref position, end);

            Require(index >= 0 && index < names.Length && number >= 0, "序列 FName 超出当前物理包名称表。");
            return new(names[index], number);
        }
    }

    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var field in value.EnumerateObject())
            {
                Require(keys.Add(field.Name), "序列 JSON 存在重复键。");
                CheckKeys(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
