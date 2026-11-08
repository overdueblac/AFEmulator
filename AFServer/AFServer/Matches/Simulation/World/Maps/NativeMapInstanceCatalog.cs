using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// Map version, physical package, and offline export identity, not runtime slots or connection wire
/// indices.
/// </summary>
public sealed record NativeMapObjectId(string PhysicalPackage, string PackageSha256, int ExportIndex);

/// <summary>
/// Logical object-serial range in the original package, pinned by caller-trusted offline artifacts.
/// </summary>
public sealed record NativeMapSourceRange(int LogicalOffset, int Bytes, string Sha256);

/// <summary>
/// Retains serialized properties and sources without synthesizing defaults for missing fields.
/// </summary>
public sealed record NativeMapExplicitProperty(string Name, string Type, int ArrayIndex,
    JsonElement SerializedValue, int PayloadRelativeOffset, int PayloadBytes, string PayloadSha256)
{
    /// <summary>
    /// Property FName index from the original package tag for incremental source-byte validation, not a
    /// runtime name-table index.
    /// </summary>
    public int SerializedNameIndex { get; init; }

    /// <summary>
    /// Type FName index from the original tag, usable only with current physical-package provenance.
    /// </summary>
    public int SerializedTypeNameIndex { get; init; }
}

/// <summary>
/// Immutable map-object evidence without construction, archetype application, attachment, or
/// effective-default evaluation.
/// </summary>
public sealed record NativeMapObject(NativeMapObjectId Id, string ObjectPath, string? ClassPath,
    int OuterReference, int ArchetypeReference, NativeMapSourceRange Source,
    string ClassAncestryStatus, int? ClassAncestryCandidateCount,
    ImmutableArray<string> ClassAncestry, ImmutableDictionary<string, NativeMapExplicitProperty> ExplicitProperties)
{
    /// <summary>
    /// Whether property-list boundaries were recovered. An empty list with false means unknown, not no
    /// properties.
    /// </summary>
    public bool HasDecodedProperties { get; init; } = true;

    /// <summary>
    /// Raw archetype path projection. Verify external-import physical packages separately; never apply
    /// templates solely by matching names.
    /// </summary>
    public string? ArchetypePath { get; init; }

    /// <summary>
    /// Native-tail source located through complete tag boundaries. Null for opaque objects; never call the
    /// entire serial a tail.
    /// </summary>
    public NativeMapSourceRange? NativeTailSource { get; init; }
}

/// <summary>
/// Original ordered Level slots, preserving holes. Serialized membership is not active Actor state.
/// </summary>
public sealed record NativeMapLevelMembers(NativeMapObject Level, ImmutableArray<NativeMapObjectId?> ActorSlots);

/// <summary>
/// Proven potential streaming dependency, granting no loading, visibility, or collision activation.
/// </summary>
public sealed record NativeMapStreamingDependency(NativeMapObject StreamingObject, string TargetPhysicalPackage);

/// <summary>
/// Actual reference chain from Level member through direct component, mesh, and BodySetup. Retains missing
/// resources/world-readiness blockers explicitly. Cross-check BodySetup fingerprints against geometry;
/// logical names cannot bind another resource version.
/// </summary>
public sealed record NativeMapStaticMeshBinding(NativeMapObjectId LevelId, int ActorSlot,
    NativeMapObject Actor, NativeMapObject Component, NativeMapObject? StaticMesh, NativeMapObject? BodySetup,
    bool IsExplicitCollisionComponent, ImmutableArray<string> MissingExplicitFields,
    ImmutableArray<string> UnsupportedExplicitFields, ImmutableArray<string> WorldInstantiationBlockers);

/// <summary>
/// Reads dedicated fingerprint-pinned map data to build ordered membership/instance dependencies. Reads
/// JSON only, without client search or converters. Establishes serialized facts only, never world matrices,
/// Actor activation, or playable-map claims.
/// </summary>
public sealed class NativeMapInstanceCatalog
{
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const int MaximumObjects = 100_000;
    private const string BinarySha256 = "b4273f2658ca94eebc559a997fdfcd02d51e77ce75b892250c1db7fb80c70b51";
    private readonly ImmutableDictionary<NativeMapObjectId, NativeMapObject> objects;

    private NativeMapInstanceCatalog(string selectedMap, string manifestSha256, string candidatesSha256,
        ImmutableDictionary<NativeMapObjectId, NativeMapObject> objects,
        ImmutableArray<NativeMapLevelMembers> levels, ImmutableArray<NativeMapStreamingDependency> streaming,
        ImmutableArray<NativeMapStaticMeshBinding> bindings)
    {
        SelectedPhysicalMap = selectedMap;
        ManifestSha256 = manifestSha256;
        CandidatesSha256 = candidatesSha256;
        this.objects = objects;
        OpaqueObjects = objects.Values.Where(x => !x.HasDecodedProperties)
            .OrderBy(x => x.Id.PhysicalPackage, StringComparer.Ordinal).ThenBy(x => x.Id.ExportIndex).ToImmutableArray();
        BodySetups = objects.Values.Where(x => x.ClassAncestry.Contains("Engine.RB_BodySetup"))
            .OrderBy(x => x.Id.PhysicalPackage, StringComparer.Ordinal).ThenBy(x => x.Id.ExportIndex).ToImmutableArray();
        Levels = levels;
        StreamingDependencies = streaming;
        StaticMeshBindings = bindings;
    }

    /// <summary>Configured physical main-map path, without inferring mode or active levels.</summary>
    public string SelectedPhysicalMap { get; }

    /// <summary>Manifest fingerprint pinned out of band by the caller during loading.</summary>
    public string ManifestSha256 { get; }

    /// <summary>
    /// Validated map-candidate content fingerprint. Cross-resource bindings must match source-map versions.
    /// </summary>
    public string CandidatesSha256 { get; }

    /// <summary>
    /// BodySetup objects from the full map set, including resources unreferenced by identified instance
    /// chains.
    /// </summary>
    public ImmutableArray<NativeMapObject> BodySetups { get; }

    /// <summary>Loaded offline-export evidence count.</summary>
    public int ObjectCount => objects.Count;

    /// <summary>
    /// Objects with identity, references, and raw serial ranges only. Unknown property/tail boundaries
    /// grant no construction or activation.
    /// </summary>
    public ImmutableArray<NativeMapObject> OpaqueObjects { get; }

    /// <summary>Serialized Level members in original slot order.</summary>
    public ImmutableArray<NativeMapLevelMembers> Levels { get; }

    /// <summary>Potential sublevel dependencies established by PackageName tags.</summary>
    public ImmutableArray<NativeMapStreamingDependency> StreamingDependencies { get; }

    /// <summary>
    /// Direct components, geometry dependencies, and unresolved world conditions per instance.
    /// </summary>
    public ImmutableArray<NativeMapStaticMeshBinding> StaticMeshBindings { get; }

    /// <summary>
    /// Queries full physical identity. Same-named objects from different versions/packages cannot
    /// substitute for one another.
    /// </summary>
    public NativeMapObject GetObject(NativeMapObjectId id) => objects.TryGetValue(id, out var value)
        ? value : throw new InvalidDataException("地图对象不属于当前固定版本的物理包集合。");

    /// <summary>
    /// Requires caller-pinned out-of-band manifest fingerprints. Loading failures expose no partially
    /// usable catalog.
    /// </summary>
    /// <param name="absoluteDirectory">
    /// Explicit absolute directory containing dedicated JSON artifacts only.
    /// </param>
    /// <param name="trustedManifestSha256">
    /// Out-of-band reviewed manifest SHA256, never self-trusted input.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// Contradictory fingerprints, member order, types, or supported property projections.
    /// </exception>
    public static NativeMapInstanceCatalog Load(string absoluteDirectory, string trustedManifestSha256)
    {
        try
        {
            return LoadCore(absoluteDirectory, trustedManifestSha256);
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException
            or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException or EncoderFallbackException)
        {
            throw new InvalidDataException("地图实例证据格式、引用或来源范围无效。", error);
        }
    }

    private static NativeMapInstanceCatalog LoadCore(string directory, string trustedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Require(Path.IsPathFullyQualified(directory), "地图证据必须从显式绝对目录加载。");
        Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "地图证据目录不能为重解析点。");
        ValidateHash(trustedHash);

        byte[] manifestBytes = ReadFile(directory, "manifest.json", 64 * 1024);

        Require(Digest(manifestBytes) == trustedHash, "地图 manifest 不符合带外信任指纹。");

        using var manifestDocument = Parse(manifestBytes);
        JsonElement manifest = manifestDocument.RootElement;

        ValidateCapabilities(manifest);
        Require(Text(manifest, "kind") == "map_instance_candidates", "地图证据种类不受支持。");

        JsonElement file = Array(manifest, "files", 1, 1)[0];

        Require(Text(file, "file") == "map-candidates.json", "地图产物文件名不受支持。");

        byte[] dataBytes = ReadFile(directory, "map-candidates.json", MaximumFileBytes);

        Require(dataBytes.Length == Integer(file, "bytes", 1, MaximumFileBytes)
            && Digest(dataBytes) == Hash(file, "sha256"), "地图产物长度或指纹不符。");

        using var document = Parse(dataBytes);
        JsonElement data = document.RootElement;

        ValidateCapabilities(data);
        Require(Text(data, "verified_binary_sha256") == BinarySha256
            && Text(manifest, "verified_binary_sha256") == BinarySha256, "地图原生格式来源不受支持。");
        Require(Array(data, "resource_packages", 0, 0).GetArrayLength() == 0,
            "外部资源包实例绑定尚未恢复，不能隐式选择依赖。");

        var packageObjects = new Dictionary<string, Dictionary<int, NativeMapObject>>(StringComparer.OrdinalIgnoreCase);
        var allObjects = ImmutableDictionary.CreateBuilder<NativeMapObjectId, NativeMapObject>();
        var levels = ImmutableArray.CreateBuilder<NativeMapLevelMembers>();

        foreach (JsonElement package in Array(data, "packages", 1, 256).EnumerateArray())
        {
            string physical = Text(package, "package");

            Require(physical.IndexOf('\\') < 0 && !Path.IsPathRooted(physical)
                && !physical.Split('/').Any(x => x is "" or "." or ".."), "物理包路径无效。");

            string packageHash = Hash(package, "source_sha256");
            JsonElement summary = package.GetProperty("summary");

            Require(Integer(summary, "version", 758, 758) == 758
                && Integer(summary, "licensee_version", 2, 2) == 2
                && Text(summary, "package") == physical, "地图包版本或物理身份不受支持。");

            int exportCount = Integer(package.GetProperty("directory_counts"), "exports", 1, MaximumObjects);
            int imports = Integer(package.GetProperty("directory_counts"), "imports", 0, MaximumObjects);
            int nameCount = Integer(package.GetProperty("directory_counts"), "names", 1, MaximumObjects);
            int logicalSize = Integer(summary, "logical_size", 1, int.MaxValue);

            Require(Integer(summary, "export_count", exportCount, exportCount) == exportCount,
                "地图目录的 export 数量矛盾。");

            var exports = new Dictionary<int, NativeMapObject>();

            Require(packageObjects.TryAdd(physical, exports), "地图物理包重复。");
            ValidatePackageNames(package, nameCount);

            foreach (JsonElement row in Array(package, "objects", exportCount, exportCount).EnumerateArray())
            {
                int index = Integer(row, "export_index", 1, exportCount);
                JsonElement source = row.GetProperty("source");
                int offset = Integer(source, "logical_offset", 0, logicalSize);
                var range = new NativeMapSourceRange(offset, Integer(source, "bytes", 1, logicalSize - offset), Hash(source, "sha256"));
                JsonElement ancestryData = row.GetProperty("class_ancestry");
                string ancestryStatus = Text(ancestryData, "status");

                Require(ancestryStatus is "resolved" or "unresolved", "类继承解析状态不受支持。");

                bool opaque = Text(row, "status") == "unsupported_class_ancestry";
                bool noClass = row.GetProperty("class_path").ValueKind == JsonValueKind.Null;
                int? ancestryCandidates = ancestryStatus == "unresolved" && !noClass
                    ? Integer(ancestryData, "candidate_count", 0, MaximumObjects) : null;
                var ancestry = Array(ancestryData, "chain", noClass ? 0 : 1, noClass ? 0 : 128).EnumerateArray()
                    .Select(x => x.GetString() ?? throw new InvalidDataException("类继承名称为空。")).ToImmutableArray();
                string? classPath = noClass ? null : Text(row, "class_path");

                Require((noClass || ancestry[0] == classPath) && ancestry.Distinct(StringComparer.Ordinal).Count() == ancestry.Length,
                    "地图类声明与继承链不符或含环。");
                Require(!noClass || opaque && ancestryStatus == "unresolved"
                    && Text(row, "class_name") == "ZeroClassStructCandidate"
                    && !ancestryData.TryGetProperty("candidate_count", out _), "未知类身份不能合成继承链或候选数。");
                Require(ancestryStatus != "resolved" || ancestry[^1] == "Core.Object", "已解析继承链没有到达 Core.Object。");
                if (opaque)
                {
                    Require(ancestryStatus == "unresolved" && !row.TryGetProperty("tag_list", out _)
                        && !row.TryGetProperty("object_prefix", out _) && !row.TryGetProperty("prefix_profile", out _)
                        && !row.TryGetProperty("native_tail", out _) && !row.TryGetProperty("level_actor_array", out _),
                        "未恢复类的 opaque 对象不得声明属性、前缀、原生尾部或 Level 数组边界。");
                }
                else Require(Text(row, "status") == "tagged_prefix_decoded", "地图对象解析状态不受支持。");
                Require(Boolean(row, "actor_candidate") == ancestry.Contains("Engine.Actor")
                    && Boolean(row, "component_candidate") == ancestry.Contains("Core.Component"), "地图对象候选类型矛盾。");

                var id = new NativeMapObjectId(physical, packageHash, index);
                var item = new NativeMapObject(id, Text(row, "path"), classPath,
                    Integer(row, "outer_index", -imports, exportCount), Integer(row, "archetype_index", -imports, exportCount),
                    range, ancestryStatus, ancestryCandidates, ancestry,
                    opaque ? ImmutableDictionary<string, NativeMapExplicitProperty>.Empty : ReadProperties(row, range.Bytes, imports, exportCount))
                {
                    HasDecodedProperties = !opaque,
                    ArchetypePath = row.GetProperty("archetype_path").ValueKind == JsonValueKind.Null
                        ? null : Text(row, "archetype_path"),
                    NativeTailSource = opaque ? null : new(checked(range.LogicalOffset
                        + Integer(row.GetProperty("native_tail"), "relative_offset", 0, range.Bytes)),
                        Integer(row.GetProperty("native_tail"), "bytes", 0, range.Bytes), Hash(row.GetProperty("native_tail"), "sha256"))
                };

                Require((item.ArchetypeReference == 0) == (item.ArchetypePath is null), "archetype 索引与路径的空值状态不符。");
                Require(exports.TryAdd(index, item) && allObjects.TryAdd(id, item), "地图 export 身份重复。");
                Require(allObjects.Count <= MaximumObjects, "地图对象总量超过限制。");
            }

            foreach (JsonElement row in package.GetProperty("objects").EnumerateArray())
            {
                NativeMapObject item = exports[Integer(row, "export_index", 1, exportCount)];

                ValidateLocalReferences(row, item, exports);
                if (item.ClassPath == "Engine.Level") levels.Add(ReadLevel(row, item, exports));
                else Require(!row.TryGetProperty("level_actor_array", out _), "非 Level 声明了原生 Actor 数组。");
            }
        }

        string selectedMap = Text(data, "selected_map");

        Require(packageObjects.ContainsKey(selectedMap) && levels.Any(x => x.Level.Id.PhysicalPackage == selectedMap),
            "选定主图没有实际 Level 成员证据。");

        var streaming = ReadStreaming(data, packageObjects, selectedMap);
        var bindings = BuildBindings(levels, packageObjects);

        foreach (JsonElement stats in new[] { data.GetProperty("statistics"), manifest.GetProperty("statistics") })
        {
            Require(Integer(stats, "objects", 0, MaximumObjects) == allObjects.Count
                && Integer(stats, "packages", 1, 256) == packageObjects.Count
                && Integer(stats, "decoded_level_actor_arrays", 1, MaximumObjects) == levels.Count
                && Integer(stats, "serialized_level_actor_slots", 0, MaximumObjects) == levels.Sum(x => x.ActorSlots.Length)
                && Integer(stats, "serialized_level_actor_references", 0, MaximumObjects) == levels.Sum(x => x.ActorSlots.Count(y => y is not null))
                && Integer(stats, "streaming_references", 0, MaximumObjects) == streaming.Length,
                "地图统计与完整实例证据不一致。");
        }

        return new(selectedMap, trustedHash, Hash(file, "sha256"), allObjects.ToImmutable(), levels.ToImmutable(), streaming, bindings);
    }

    /// <summary>
    /// Checks consecutive tag boundaries, retaining only present source fields and unresolved value states.
    /// </summary>
    private static ImmutableDictionary<string, NativeMapExplicitProperty> ReadProperties(JsonElement row, int serialBytes,
        int importCount, int exportCount)
    {
        JsonElement tags = row.GetProperty("tag_list");
        int next = Integer(tags, "relative_offset", 0, serialBytes);
        int start = next;

        Require(next == Integer(row.GetProperty("object_prefix"), "tag_relative_offset", next, next), "属性起点与对象前缀不符。");

        var result = ImmutableDictionary.CreateBuilder<string, NativeMapExplicitProperty>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement tag in Array(tags, "properties", 0, 8192).EnumerateArray())
        {
            string name = FName(tag.GetProperty("name"));
            string type = FName(tag.GetProperty("type"));
            int arrayIndex = Integer(tag, "array_index", 0, 65535);
            JsonElement source = tag.GetProperty("source");
            int tagOffset = Integer(source, "tag_relative_offset", next, next);
            int headerBytes = type is "StructProperty" or "ByteProperty" ? 32 : type == "BoolProperty" ? 25 : 24;
            int payloadOffset = Integer(source, "payload_relative_offset", checked(tagOffset + headerBytes), serialBytes);

            Require(payloadOffset == tagOffset + headerBytes, "属性 tag 头长度不符合原生类型。");

            int payloadBytes = Integer(source, "payload_bytes", 0, serialBytes - payloadOffset);
            string payloadHash = Hash(source, "payload_sha256");
            JsonElement value = tag.GetProperty("serialized_value");

            ValidateExplicitValue(tag, type, value, payloadBytes, payloadHash, importCount, exportCount);

            var property = new NativeMapExplicitProperty(name, type, arrayIndex, value.Clone(), payloadOffset, payloadBytes, payloadHash)
            {
                SerializedNameIndex = tag.GetProperty("name")[0].GetInt32(),
                SerializedTypeNameIndex = tag.GetProperty("type")[0].GetInt32()
            };
            string key = arrayIndex == 0 ? name : name + "[" + arrayIndex.ToString(CultureInfo.InvariantCulture) + "]";

            Require(result.TryAdd(key, property), "同一属性及数组下标重复，不能猜测有效覆盖顺序。");
            next = checked(payloadOffset + payloadBytes);
        }

        Require(next <= serialBytes - 8, "None 终止符超出对象 serial 边界。");
        Require(Integer(tags, "terminator_relative_offset", next, next) == next
            && FName(tags.GetProperty("terminator")) == "None"
            && Integer(tags, "bytes", next + 8 - start, next + 8 - start) == next + 8 - start,
            "属性列表未连续覆盖到 None 终止符。");

        JsonElement tail = row.GetProperty("native_tail");

        Require(Integer(tail, "relative_offset", next + 8, next + 8) == next + 8
            && Integer(tail, "bytes", serialBytes - next - 8, serialBytes - next - 8) == serialBytes - next - 8,
            "属性列表与 native tail 边界不符。");
        Hash(tail, "sha256");
        return result.ToImmutable();
    }

    /// <summary>
    /// Rebuilds payloads with recovered encodings and checks source hashes so JSON values cannot diverge
    /// from original bits.
    /// </summary>
    private static void ValidateExplicitValue(JsonElement tag, string type, JsonElement value, int bytes, string hash,
        int importCount, int exportCount)
    {
        string status = Text(value, "status");

        if (status == "unsupported") return;
        Require(status == "decoded", "属性值状态不受支持。");

        string encoding = Text(value, "encoding");
        byte[]? payload = null;

        switch (type)
        {
            case "ObjectProperty":
                Require(encoding == "package_object_index_i32", "对象引用编码不受支持。");
                payload = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(payload, Integer(value, "object_index", -importCount, exportCount));
                break;
            case "IntProperty":
                Require(encoding == "i32_le", "整数属性编码不受支持。");
                payload = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(payload, Integer(value, "value", int.MinValue, int.MaxValue));
                break;
            case "ByteProperty":
                bool hasEnum = FName(tag.GetProperty("type_metadata").GetProperty("enum_name")) != "None";

                Require(hasEnum ? encoding == "enum_member_fname" : encoding == "u8", "字节属性编码与枚举声明不符。");
                payload = hasEnum ? NamePayload(value.GetProperty("value")) : [(byte)Integer(value, "value", 0, 255)];
                break;
            case "FloatProperty":
                Require(encoding == "ieee754_binary32_le", "浮点编码不受支持。");
                payload = new byte[4];
                WriteFloat(value.GetProperty("value"), Text(value, "bits_u32_hex"), payload);
                break;
            case "NameProperty":
                Require(encoding == "package_fname_i32_pair", "名称属性编码不受支持。");
                payload = NamePayload(value.GetProperty("value"));
                break;
            case "StrProperty":
                payload = StringPayload(value, encoding);
                break;
            case "StructProperty" when encoding is "three_binary32_le" or "three_i32_le":
                JsonElement declaration = tag.GetProperty("declaration_binding");
                string structure = Text(declaration, "struct_path");
                bool vector = encoding == "three_binary32_le";

                Require(Text(declaration, "status") == "unique_declaration" && Text(declaration, "kind") == "StructProperty"
                    && Integer(declaration, "array_dimension", 1, 1) == 1
                    && (vector ? structure is "Core.Object.Vector" or "Core.Object.VectorNoCompress" : structure == "Core.Object.Rotator")
                    && FName(tag.GetProperty("type_metadata").GetProperty("struct_name")) == structure.Split('.')[^1],
                    "固定结构值缺少唯一真实声明绑定。");

                JsonElement bits = Array(value, "bits_u32_hex", 3, 3);
                string[] components = vector ? ["X", "Y", "Z"] : ["Pitch", "Yaw", "Roll"];

                payload = new byte[12];
                for (int i = 0; i < 3; i++)
                {
                    string raw = bits[i].GetString() ?? "";

                    if (vector) WriteFloat(value.GetProperty("value").GetProperty(components[i]), raw, payload.AsSpan(i * 4, 4));
                    else
                    {
                        uint word = ParseWord(raw);

                        Require(value.GetProperty("value").GetProperty(components[i]).GetInt32() == unchecked((int)word), "Rotator 原始整数与投影不符。");
                        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 4, 4), word);
                    }
                }
                break;
            case "BoolProperty":
                Require(encoding == "tag_byte_nonzero" && bytes == 0
                    && Boolean(value, "value") == (Integer(tag.GetProperty("type_metadata"), "bool_byte", 0, 255) != 0),
                    "布尔属性 tag 字节与投影不符。");
                payload = [];
                break;
            default:
                throw new InvalidDataException("属性声明 decoded，但其类型/编码尚无独立 payload 校验；必须显式标记 unsupported。");
        }

        if (payload is not null) Require(payload.Length == bytes && Digest(payload) == hash, "显式属性投影不符合原始 payload 指纹。");
    }

    private static byte[] NamePayload(JsonElement name)
    {
        Require(name.ValueKind == JsonValueKind.Array && name.GetArrayLength() == 3
            && name[0].GetInt32() >= 0 && name[1].GetInt32() >= 0 && name[2].ValueKind == JsonValueKind.String,
            "名称属性的 FName 无效。");

        byte[] payload = new byte[8];

        BinaryPrimitives.WriteInt32LittleEndian(payload, name[0].GetInt32());
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), name[1].GetInt32());
        return payload;
    }

    /// <summary>
    /// Positive lengths zero-extend bytes; negative lengths use UTF-16. Both include counts/terminators
    /// without system code pages.
    /// </summary>
    private static byte[] StringPayload(JsonElement value, string encoding)
    {
        JsonElement text = value.GetProperty("value");

        Require(text.ValueKind == JsonValueKind.String && text.GetString()!.Length <= 65535, "FString 投影类型或长度无效。");

        string content = text.GetString()!;

        Require(encoding is "empty_fstring" or "byte_to_u16" or "utf16_le", "FString 编码不受支持。");
        if (encoding == "empty_fstring")
        {
            Require(content.Length == 0, "零计数 FString 带有文本。");
            return new byte[4];
        }

        bool wide = encoding == "utf16_le";

        Require(wide || content.All(x => x <= byte.MaxValue), "正计数 FString 不能包含超出字节范围的字符。");

        byte[] result = new byte[4 + (content.Length + 1) * (wide ? 2 : 1)];

        BinaryPrimitives.WriteInt32LittleEndian(result, wide ? -(content.Length + 1) : content.Length + 1);
        if (wide) new UnicodeEncoding(false, false, true).GetBytes(content, result.AsSpan(4));
        else for (int i = 0; i < content.Length; i++) result[i + 4] = (byte)content[i];
        return result;
    }

    /// <summary>
    /// The artifact lacks a complete name table. Cross-check observed FName numeric indices/text without
    /// claiming independent recovery of missing names.
    /// </summary>
    private static void ValidatePackageNames(JsonElement package, int nameCount)
    {
        var names = new Dictionary<int, string>();

        Visit(package);

        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) Visit(property.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() == 3 && value[0].ValueKind == JsonValueKind.Number
                    && value[1].ValueKind == JsonValueKind.Number && value[2].ValueKind == JsonValueKind.String)
                {
                    int index = value[0].GetInt32();
                    string text = value[2].GetString()!;

                    Require(index >= 0 && index < nameCount && value[1].GetInt32() >= 0 && text.Length is > 0 and <= 4096,
                        "包内 FName 数值或文本范围无效。");
                    Require(!names.TryGetValue(index, out string? previous) || previous == text, "同一包内 FName 数值索引对应多个文本。");
                    names[index] = text;
                }
                else foreach (var item in value.EnumerateArray()) Visit(item);
            }
        }
    }

    private static void ValidateLocalReferences(JsonElement row, NativeMapObject item, Dictionary<int, NativeMapObject> exports)
    {
        if (item.OuterReference > 0)
            Require(exports[item.OuterReference].ObjectPath == Text(row, "outer_path"), "对象 outer 路径与物理 export 不符。");

        var seen = new HashSet<int> { item.Id.ExportIndex };
        int current = item.OuterReference;

        while (current > 0)
        {
            Require(seen.Add(current) && seen.Count <= 128, "地图对象 outer 链含环或超过深度限制。");
            current = exports[current].OuterReference;
        }

        if (item.ArchetypeReference > 0)
            Require(exports[item.ArchetypeReference].ObjectPath == Text(row, "archetype_path"), "对象 archetype 路径与物理 export 不符。");
        foreach (var property in item.ExplicitProperties.Values.Where(x => x.Type == "ObjectProperty"))
        {
            JsonElement value = property.SerializedValue;

            Require(Text(value, "status") == "decoded", "对象引用缺少已解码索引。");

            int target = value.GetProperty("object_index").GetInt32();

            if (target > 0) Require(exports[target].ObjectPath == Text(value, "object_path"), "对象引用路径与物理 export 不符。");
            if (target == 0) Require(value.GetProperty("object_path").ValueKind == JsonValueKind.Null, "空引用带有对象路径。");
        }
    }

    /// <summary>
    /// Rebuilds Level prefixes by owner, count, and original slot order, without compacting holes or
    /// deriving order from Outer sets.
    /// </summary>
    private static NativeMapLevelMembers ReadLevel(JsonElement row, NativeMapObject level, Dictionary<int, NativeMapObject> exports)
    {
        JsonElement array = row.GetProperty("level_actor_array");

        Require(Text(array, "status") == "decoded" && Text(array, "profile") == "AF_758_2_Level_owner_and_Actor_array",
            "Level 原生数组格式不受支持。");

        int count = Integer(array, "actor_count", 0, MaximumObjects);

        Require(Integer(array, "owner_object_index", level.Id.ExportIndex, level.Id.ExportIndex) == level.Id.ExportIndex,
            "Level owner 引用不符。");

        JsonElement source = array.GetProperty("source");
        int offset = Integer(source, "relative_offset", 0, level.Source.Bytes);
        int length = checked(8 + count * 4);

        Require(length <= level.Source.Bytes - offset && Integer(source, "bytes", length, length) == length
            && Integer(row.GetProperty("native_tail"), "relative_offset", offset, offset) == offset,
            "Level 数组长度或起点不符。");

        byte[] originalWords = new byte[length];

        BinaryPrimitives.WriteInt32LittleEndian(originalWords, level.Id.ExportIndex);
        BinaryPrimitives.WriteInt32LittleEndian(originalWords.AsSpan(4), count);

        var slots = ImmutableArray.CreateBuilder<NativeMapObjectId?>(count);
        var members = new HashSet<int>();

        foreach (JsonElement reference in Array(array, "actor_references", count, count).EnumerateArray())
        {
            int slot = slots.Count;

            Require(Integer(reference, "slot", slot, slot) == slot
                && Integer(reference, "relative_offset", offset + 8 + slot * 4, offset + 8 + slot * 4) == offset + 8 + slot * 4,
                "Level Actor 槽位或原始顺序不符。");

            int index = Integer(reference, "object_index", 0, exports.Count);

            BinaryPrimitives.WriteInt32LittleEndian(originalWords.AsSpan(8 + slot * 4), index);
            if (index == 0)
            {
                Require(reference.GetProperty("object_path").ValueKind == JsonValueKind.Null, "Level 空槽带有对象路径。");
                slots.Add(null);
                continue;
            }

            NativeMapObject actor = exports[index];

            Require(members.Add(index) && actor.OuterReference == level.Id.ExportIndex && actor.ClassAncestry.Contains("Engine.Actor")
                && actor.ObjectPath == Text(reference, "object_path"), "Level 成员重复、类型、属主或路径不符。");
            slots.Add(actor.Id);
        }

        Require(Digest(originalWords) == Hash(source, "sha256"), "Level owner/数量/槽位重建字节与原始指纹不符。");

        JsonElement remaining = array.GetProperty("remaining_native_tail");

        Require(Integer(remaining, "relative_offset", offset + length, offset + length) == offset + length
            && Integer(remaining, "bytes", level.Source.Bytes - offset - length, level.Source.Bytes - offset - length)
                == level.Source.Bytes - offset - length, "Level 数组与剩余 native tail 重叠或不连续。");
        Hash(remaining, "sha256");
        return new(level, slots.MoveToImmutable());
    }

    private static ImmutableArray<NativeMapStreamingDependency> ReadStreaming(JsonElement data,
        Dictionary<string, Dictionary<int, NativeMapObject>> packages, string selectedMap)
    {
        var result = ImmutableArray.CreateBuilder<NativeMapStreamingDependency>();
        var seen = new HashSet<NativeMapObjectId>();
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedMap };

        foreach (JsonElement edge in Array(data, "streaming_edges", 0, 256).EnumerateArray())
        {
            string from = Text(edge, "from_package");

            Require(packages.ContainsKey(from), "流式依赖源包不存在。");

            NativeMapObject item = packages[from][Integer(edge, "export_index", 1, packages[from].Count)];

            Require(item.ClassAncestry.Contains("Engine.LevelStreaming") && seen.Add(item.Id)
                && item.ObjectPath == Text(edge, "path") && Text(edge, "status") == "unique_package", "流式对象身份或唯一解析状态不符。");

            NativeMapExplicitProperty property = item.ExplicitProperties["PackageName"];

            Require(property.Type == "NameProperty" && Text(property.SerializedValue, "status") == "decoded"
                && Text(property.SerializedValue, "encoding") == "package_fname_i32_pair", "流式 PackageName 不是明确名称属性。");
            ValidateStreamingPropertySource(edge.GetProperty("property_source"), property);

            string name = FName(property.SerializedValue.GetProperty("value"));

            Require(name == Text(edge, "package_name"), "流式依赖名称与原始显式属性不符。");

            JsonElement candidates = Array(edge, "candidate_packages", 1, 1);
            string target = candidates[0].GetString() ?? "";

            Require(packages.ContainsKey(target) && Path.GetFileNameWithoutExtension(target) == name, "流式依赖目标不是唯一物理包。");
            result.Add(new(item, target));
        }

        foreach (JsonElement package in data.GetProperty("packages").EnumerateArray())
        {
            string physical = Text(package, "package");
            var edges = result.Where(x => x.StreamingObject.Id.PhysicalPackage == physical)
                .ToDictionary(x => x.StreamingObject.Id.ExportIndex);
            int explicitStreamingObjects = packages[physical].Values.Count(x => x.ClassAncestry.Contains("Engine.LevelStreaming")
                && x.ExplicitProperties.ContainsKey("PackageName"));

            Require(explicitStreamingObjects == edges.Count, "流式边未完整覆盖包内明确 PackageName 属性。");

            var referenced = new HashSet<int>();

            foreach (JsonElement reference in Array(package, "streaming_references", edges.Count, edges.Count).EnumerateArray())
            {
                int index = Integer(reference, "export_index", 1, packages[physical].Count);

                Require(edges.TryGetValue(index, out NativeMapStreamingDependency? edge) && referenced.Add(index),
                    "包内流式引用投影缺失或重复。");

                NativeMapExplicitProperty property = edge!.StreamingObject.ExplicitProperties["PackageName"];

                Require(Text(reference, "path") == edge.StreamingObject.ObjectPath
                    && Text(reference, "package_name") == FName(property.SerializedValue.GetProperty("value")),
                    "包内流式引用投影与对象名称不符。");
                ValidateStreamingPropertySource(reference.GetProperty("property_source"), property);
            }

            var cooked = Array(package.GetProperty("summary"), "additional_packages_to_cook", 0, 256).EnumerateArray()
                .Select(x => x.GetString() ?? throw new InvalidDataException("额外 cook 包名称无效。"))
                .ToHashSet(StringComparer.Ordinal);

            Require(edges.Values.All(x => cooked.Contains(Path.GetFileNameWithoutExtension(x.TargetPhysicalPackage))),
                "流式目标未出现在原始 summary 的附加 cook 依赖中。");
        }

        bool changed;

        do
        {
            changed = false;
            foreach (var edge in result)
                if (reached.Contains(edge.StreamingObject.Id.PhysicalPackage)) changed |= reached.Add(edge.TargetPhysicalPackage);
        } while (changed);
        Require(reached.Count == packages.Count, "地图集合含不能从主图 PackageName 引用到达的包。");
        return result.ToImmutable();
    }

    private static void ValidateStreamingPropertySource(JsonElement source, NativeMapExplicitProperty property)
    {
        Require(Integer(source, "payload_relative_offset", property.PayloadRelativeOffset, property.PayloadRelativeOffset) == property.PayloadRelativeOffset
            && Integer(source, "payload_bytes", property.PayloadBytes, property.PayloadBytes) == property.PayloadBytes
            && Integer(source, "tag_relative_offset", property.PayloadRelativeOffset - 24, property.PayloadRelativeOffset - 24)
                == property.PayloadRelativeOffset - 24 && Hash(source, "payload_sha256") == property.PayloadSha256,
            "流式引用投影与实际 PackageName 属性来源不符。");
    }

    /// <summary>
    /// Builds geometry requests from explicit object properties/direct relationships, reporting missing and
    /// uninterpretable values separately.
    /// </summary>
    private static ImmutableArray<NativeMapStaticMeshBinding> BuildBindings(IEnumerable<NativeMapLevelMembers> levels,
        Dictionary<string, Dictionary<int, NativeMapObject>> packages)
    {
        var result = ImmutableArray.CreateBuilder<NativeMapStaticMeshBinding>();

        foreach (var level in levels)
        {
            Dictionary<int, NativeMapObject> exports = packages[level.Level.Id.PhysicalPackage];

            for (int slot = 0; slot < level.ActorSlots.Length; slot++)
            {
                if (level.ActorSlots[slot] is not { } actorId) continue;

                NativeMapObject actor = exports[actorId.ExportIndex];

                if (!actor.ExplicitProperties.ContainsKey("StaticMeshComponent")) continue;

                NativeMapObject? component = Resolve(actor, "StaticMeshComponent", exports);

                Require(component is not null && component.OuterReference == actor.Id.ExportIndex
                    && component.ClassAncestry.Contains("Engine.StaticMeshComponent"), "StaticMeshComponent 不是 Actor 的真实直属组件。");

                NativeMapObject? mesh = Resolve(component!, "StaticMesh", exports);

                Require(mesh is null || mesh.ClassPath == "Engine.StaticMesh", "StaticMesh 引用目标类型不符。");

                NativeMapObject? body = mesh is null ? null : Resolve(mesh, "BodySetup", exports);

                Require(body is null || body.ClassAncestry.Contains("Engine.RB_BodySetup") && body.OuterReference == mesh!.Id.ExportIndex,
                    "BodySetup 类型或网格属主不符。");

                NativeMapObject? collisionComponent = Resolve(actor, "CollisionComponent", exports);
                var missing = ImmutableArray.CreateBuilder<string>();
                var unsupported = ImmutableArray.CreateBuilder<string>();

                foreach (string name in new[] { "Location", "Rotation", "DrawScale", "DrawScale3D", "Base", "bCollideActors", "bBlockActors" })
                    InspectField(actor, "Actor.", name);
                foreach (string name in new[] { "Translation", "Rotation", "Scale", "Scale3D", "AbsoluteTranslation", "AbsoluteRotation",
                    "AbsoluteScale", "CollideActors", "BlockActors", "BlockZeroExtent", "BlockNonZeroExtent", "BlockRigidBody" })
                    InspectField(component!, "Component.", name);

                var blockers = ImmutableArray.CreateBuilder<string>();

                blockers.AddRange(new[] { "native_construction_and_effective_defaults_unresolved", "attachment_and_world_transform_unresolved",
                    "effective_collision_flags_unresolved", "level_streaming_activation_unresolved" });
                if (level.Level.ClassAncestryStatus != "resolved") blockers.Add("level_type_ancestry_unresolved");
                if (actor.ClassAncestryStatus != "resolved") blockers.Add("actor_type_ancestry_unresolved");
                if (component!.ClassAncestryStatus != "resolved") blockers.Add("component_type_ancestry_unresolved");
                if (mesh is not null && mesh.ClassAncestryStatus != "resolved") blockers.Add("static_mesh_type_ancestry_unresolved");
                if (body is not null && body.ClassAncestryStatus != "resolved") blockers.Add("body_setup_type_ancestry_unresolved");
                if (unsupported.Count > 0) blockers.Add("explicit_world_inputs_unsupported");
                if (mesh is null) blockers.Add("explicit_static_mesh_missing_or_null");
                if (body is null) blockers.Add("explicit_body_setup_missing_or_null");
                if (collisionComponent?.Id != component!.Id) blockers.Add("component_not_explicit_actor_collision_component");
                result.Add(new(level.Level.Id, slot, actor, component!, mesh, body, collisionComponent?.Id == component!.Id,
                    missing.ToImmutable(), unsupported.ToImmutable(), blockers.ToImmutable()));

                void InspectField(NativeMapObject item, string prefix, string name)
                {
                    if (!item.ExplicitProperties.TryGetValue(name, out var property)) missing.Add(prefix + name);
                    else if (Text(property.SerializedValue, "status") != "decoded") unsupported.Add(prefix + name);
                }
            }
        }

        return result.ToImmutable();
    }

    private static NativeMapObject? Resolve(NativeMapObject source, string propertyName, Dictionary<int, NativeMapObject> exports)
    {
        if (!source.ExplicitProperties.TryGetValue(propertyName, out var property)) return null;
        Require(property.Type == "ObjectProperty" && Text(property.SerializedValue, "status") == "decoded", "实例依赖不是明确对象属性。");

        int index = property.SerializedValue.GetProperty("object_index").GetInt32();

        Require(index >= 0, "实例几何链含外部导入；必须先建立可信物理包解析，不能按名称猜选。");
        return index == 0 ? null : exports[index];
    }

    private static void ValidateCapabilities(JsonElement value)
    {
        Require(Integer(value, "schema_version", 1, 1) == 1 && Boolean(value, "candidate_only")
            && !Boolean(value, "client_files_required_at_runtime") && !Boolean(value, "map_backend_complete")
            && !Boolean(value, "collision_geometry_complete"), "地图候选错误声明了完整世界或运行时客户端依赖。");
    }

    private static void WriteFloat(JsonElement projected, string rawBits, Span<byte> target)
    {
        uint bits = ParseWord(rawBits);
        float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));

        Require(float.IsFinite(value) && projected.ValueKind == JsonValueKind.Number
            && projected.GetDouble() == (double)value && BitConverter.SingleToInt32Bits(projected.GetSingle()) == unchecked((int)bits),
            "显式 binary32 值不有限或与原始 bits 不符。");
        BinaryPrimitives.WriteUInt32LittleEndian(target, bits);
    }

    private static uint ParseWord(string value)
    {
        Require(value.Length == 8, "原始 word 长度无效。");
        return uint.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string FName(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3
            && value[0].GetInt32() >= 0 && value[1].GetInt32() == 0 && value[2].ValueKind == JsonValueKind.String,
            "绑定字段的 FName 数组或数值后缀不受支持。");
        return value[2].GetString()!;
    }

    private static byte[] ReadFile(string directory, string name, int maximumBytes)
    {
        string path = Path.Combine(directory, name);

        Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "地图证据文件不能为重解析点。");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Require(stream.Length is > 0 && stream.Length <= maximumBytes, "地图证据文件长度超限。");

        byte[] bytes = new byte[(int)stream.Length];

        stream.ReadExactly(bytes);
        return bytes;
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });

        try { CheckUniqueNames(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void CheckUniqueNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "地图 JSON 含重复字段。");
                CheckUniqueNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUniqueNames(item);
    }

    private static JsonElement Array(JsonElement parent, string name, int minimum, int maximum)
    {
        JsonElement value = parent.GetProperty(name);

        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= minimum && value.GetArrayLength() <= maximum,
            $"地图数组范围无效：{name}。");
        return value;
    }

    private static int Integer(JsonElement parent, string name, int minimum, int maximum)
    {
        JsonElement value = parent.GetProperty(name);

        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) && parsed >= minimum && parsed <= maximum,
            $"地图整数范围无效：{name}。");
        return value.GetInt32();
    }

    private static bool Boolean(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);

        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"地图布尔字段无效：{name}。");
        return value.GetBoolean();
    }

    private static string Text(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);

        Require(value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 4096 }, $"地图文本字段无效：{name}。");
        return value.GetString()!;
    }

    private static string Hash(JsonElement parent, string name)
    {
        string value = Text(parent, name);

        ValidateHash(value);
        return value;
    }

    private static void ValidateHash(string value) => Require(value is { Length: 64 }
        && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f'), "地图 SHA256 格式无效。");

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
