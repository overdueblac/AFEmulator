using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AFServer.AssaultFire.Matches.Objects;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// Array, inner-object property, and element-class declarations proven by raw serials, without claiming
/// native construction.
/// </summary>
public sealed record NativeMapStreamingDeclaration(Ue3ExportIdentity ArrayProperty,
    Ue3ObjectSourceLocation ArraySource, Ue3ExportIdentity InnerProperty,
    Ue3ObjectSourceLocation InnerSource, Ue3ClassIdentity ElementClass);

/// <summary>
/// Original StreamingLevels slots, with offsets relative to WorldInfo serial start. Preserves holes,
/// duplicate references, and order. Target Levels remain serialized membership evidence.
/// </summary>
public sealed record NativeMapStreamingSlot(int Slot, int PayloadRelativeOffset,
    NativeMapStreamingDependency? Dependency, NativeMapLevelMembers? TargetLevel);

/// <summary>
/// WorldInfo in Level membership with its explicit array. Missing differs from explicitly empty; no
/// defaults are applied.
/// </summary>
public sealed record NativeMapWorldStreamingEvidence(NativeMapObject WorldInfo, NativeMapLevelMembers OwnerLevel,
    NativeMapExplicitProperty? Property, NativeMapSourceRange? ArraySource,
    ImmutableArray<NativeMapStreamingSlot> Slots)
{
    /// <summary>
    /// Whether an explicit array exists and was fully read. false does not imply no runtime streaming
    /// dependencies.
    /// </summary>
    public bool HasExplicitArray => Property is not null;
}

/// <summary>
/// Cross-binds raw WorldInfo.StreamingLevels, actual declarations, and physical-map dependencies into
/// immutable ordered evidence. Supports exact Engine.WorldInfo types only; derived same-name overrides
/// remain unrecovered. Executes no Kismet and infers no loaded/visible/request state or effective
/// collision. Reading success authorizes no world activation.
/// </summary>
public sealed class NativeMapStreamingCatalog
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumSerialBytes = 1024 * 1024;
    private const int MaximumSlotsPerWorld = 4096;
    private const int MaximumWorlds = 256;

    private NativeMapStreamingCatalog(string evidenceSha256, NativeMapStreamingDeclaration declaration,
        ImmutableArray<NativeMapWorldStreamingEvidence> worlds,
        ImmutableArray<NativeMapStreamingDependency> unreferenced)
    {
        EvidenceSha256 = evidenceSha256;
        Declaration = declaration;
        Worlds = worlds;
        UnreferencedDependencies = unreferenced;
    }

    /// <summary>Caller-pinned, validated out-of-band incremental-evidence fingerprint.</summary>
    public string EvidenceSha256 { get; }

    /// <summary>Original ArrayProperty -> inner ObjectProperty -> LevelStreaming declaration chain.</summary>
    public NativeMapStreamingDeclaration Declaration { get; }

    /// <summary>
    /// WorldInfo evidence preserving Level/Actor slot order, including objects without explicit arrays.
    /// </summary>
    public ImmutableArray<NativeMapWorldStreamingEvidence> Worlds { get; }

    /// <summary>
    /// Dependencies with explicit PackageName but no decoded-array references. This does not justify
    /// deletion or an unloaded-state conclusion.
    /// </summary>
    public ImmutableArray<NativeMapStreamingDependency> UnreferencedDependencies { get; }

    /// <summary>
    /// World activation conditions still requiring actual construction, defaults, and execution-chain
    /// recovery.
    /// </summary>
    public ImmutableArray<string> RuntimeStateBlockers { get; } =
        ["native_construction_and_effective_defaults_unresolved", "kismet_and_streaming_requests_unresolved",
            "loaded_and_visible_level_state_unresolved", "effective_collision_activation_unresolved"];

    /// <summary>
    /// Reads dedicated incremental-serial JSON. Any contradiction rejects the entire batch without
    /// modifying supplied map/identity catalogs.
    /// </summary>
    /// <param name="path">Explicit absolute evidence-file path.</param>
    /// <param name="expectedSha256">File SHA256 pinned out of band by a trusted caller.</param>
    /// <param name="map">
    /// Map catalog with validated sources, members, and potential PackageName dependencies.
    /// </param>
    /// <param name="identities">Identity catalog with explicit actual Engine/Core package selection.</param>
    public static NativeMapStreamingCatalog Load(string path, string expectedSha256,
        NativeMapInstanceCatalog map, Ue3ObjectIdentityCatalog identities)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(identities);

        try
        {
            Require(Path.IsPathFullyQualified(path), "流式证据必须使用显式绝对路径。");
            Require((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) == 0
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "流式证据路径不能为重解析点。");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            Require(stream.Length is > 0 and <= MaximumFileBytes, "流式证据文件为空或超过预算。");

            byte[] bytes = new byte[checked((int)stream.Length)];

            stream.ReadExactly(bytes);
            Require(Hash(bytes) == expectedSha256, "流式证据与外部信任锚不符。");

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });

            NoDuplicateKeys(document.RootElement);

            var root = document.RootElement;

            Require(root.GetProperty("schema_version").GetInt32() == 1
                && root.GetProperty("candidate_only").GetBoolean()
                && !root.GetProperty("runtime_streaming_state_complete").GetBoolean()
                && root.GetProperty("verified_binary_sha256").GetString() == Ue3ObjectIdentityLoader.VerifiedBinarySha256,
                "流式证据 schema、原始版本或研究边界不符。");
            Require(root.GetProperty("map_manifest_sha256").GetString() == map.ManifestSha256
                && root.GetProperty("map_candidates_sha256").GetString() == map.CandidatesSha256,
                "流式证据和地图不属于同一来源集合。");

            NativeMapStreamingDeclaration declaration = ReadDeclaration(root.GetProperty("declarations"), identities);
            var sourceRows = ReadWorldRows(root.GetProperty("worlds"));
            var dependencies = map.StreamingDependencies.ToDictionary(x => x.StreamingObject.Id);
            var referenced = new HashSet<NativeMapObjectId>();
            var worlds = ImmutableArray.CreateBuilder<NativeMapWorldStreamingEvidence>();
            var usedRows = new HashSet<NativeMapObjectId>();
            var checkedClasses = new Dictionary<string, ImmutableArray<Ue3ClassIdentity>>(StringComparer.Ordinal);
            var worldInfoClass = identities.GetClass(declaration.ArrayProperty.PackageId, declaration.ArrayProperty.OuterReference);

            foreach (NativeMapLevelMembers level in map.Levels)
            foreach (NativeMapObjectId? id in level.ActorSlots)
            {
                if (id is null) continue;

                NativeMapObject item = map.GetObject(id);

                if (!item.ClassAncestry.Contains("Engine.WorldInfo")) continue;
                Require(worlds.Count < MaximumWorlds, "WorldInfo 数量超过预算。");
                Require(item.ClassPath == worldInfoClass.Definition.DisplayPath,
                    "派生 WorldInfo 的同名字段覆盖尚未恢复，不能直接应用父类数组声明。");
                ValidateClass(item, worldInfoClass);
                Require(item.OuterReference == level.Level.Id.ExportIndex,
                    "WorldInfo 不属于当前序列化 Level。");

                if (!item.ExplicitProperties.TryGetValue("StreamingLevels", out var property))
                {
                    Require(!sourceRows.ContainsKey(item.Id), "没有显式 StreamingLevels 的 WorldInfo 不得附加数组来源。");
                    worlds.Add(new(item, level, null, null, []));
                    continue;
                }

                Require(property.Type == "ArrayProperty" && property.ArrayIndex == 0
                    && property.Name == "StreamingLevels" && sourceRows.TryGetValue(item.Id, out _)
                    && usedRows.Add(item.Id), "流式数组属性类型、槽位或增量来源无效。");

                byte[] serial = ReadSerial(sourceRows[item.Id], item.Source.LogicalOffset, item.Source.Bytes, item.Source.Sha256);
                ReadOnlySpan<byte> payload = ReadArrayPayload(serial, property);
                int count = ReadInt32(payload, 0);

                Require(count is >= 0 and <= MaximumSlotsPerWorld && payload.Length == 4L + 4L * count,
                    "StreamingLevels 数量、截断或尾部范围无效。");

                var slots = ImmutableArray.CreateBuilder<NativeMapStreamingSlot>(count);
                NativeMapObject ownerWorld = map.GetObject(level.Level.Id with { ExportIndex = level.Level.OuterReference });

                Require(ownerWorld.ClassPath == "Engine.World", "流式数组所在 Level 缺少原始 World outer。");
                for (int slot = 0; slot < count; slot++)
                {
                    int offset = checked(property.PayloadRelativeOffset + 4 + slot * 4);
                    int reference = ReadInt32(payload, 4 + slot * 4);

                    if (reference == 0)
                    {
                        slots.Add(new(slot, offset, null, null));
                        continue;
                    }

                    Require(reference > 0, "外部流式对象引用尚未恢复物理解析，不得按名称替代。");

                    NativeMapObject target = map.GetObject(item.Id with { ExportIndex = reference });

                    ValidateClass(target, declaration.ElementClass);
                    Require(target.OuterReference == ownerWorld.Id.ExportIndex
                        && dependencies.TryGetValue(target.Id, out _), "流式对象的 World outer 或明确物理依赖缺失。");

                    NativeMapStreamingDependency dependency = dependencies[target.Id];
                    NativeMapLevelMembers[] targets = map.Levels.Where(x => x.Level.Id.PhysicalPackage == dependency.TargetPhysicalPackage).ToArray();

                    Require(targets.Length == 1, "流式目标物理包没有唯一的已恢复 Level。");
                    slots.Add(new(slot, offset, dependency, targets[0]));
                    referenced.Add(target.Id);
                }

                worlds.Add(new(item, level, property, new(checked(item.Source.LogicalOffset + property.PayloadRelativeOffset),
                    property.PayloadBytes, property.PayloadSha256), slots.MoveToImmutable()));
            }

            Require(worlds.Count > 0 && usedRows.Count == sourceRows.Count, "流式增量来源未被真实 WorldInfo 显式数组完整消费。");
            return new(expectedSha256, declaration, worlds.ToImmutable(),
                map.StreamingDependencies.Where(x => !referenced.Contains(x.StreamingObject.Id)).ToImmutableArray());

            void ValidateClass(NativeMapObject item, Ue3ClassIdentity expected)
            {
                string classPath = item.ClassPath ?? throw new InvalidDataException("流式对象缺少真实类身份。");

                Require(item.HasDecodedProperties, "流式对象属性边界尚未恢复。");
                if (!checkedClasses.TryGetValue(classPath, out var ancestry))
                {
                    var type = identities.ResolveUniqueLogicalPath(classPath.Split('.').Select(x => new Ue3ObjectName(x, 0)).ToArray());

                    ancestry = identities.ResolveClassAncestry(type.PackageId, type.ExportIndex);
                    checkedClasses.Add(classPath, ancestry);
                }

                Require(item.ClassAncestryStatus == "resolved"
                    && item.ClassAncestry.SequenceEqual(ancestry.Select(x => x.Definition.DisplayPath))
                    && ancestry.Any(x => x.Definition.ObjectId == expected.Definition.ObjectId),
                    "流式对象或 WorldInfo 的真实类型、继承链与声明不符。");
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or OverflowException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("流式增量证据格式、来源或引用无效。", error);
        }
    }

    private static NativeMapStreamingDeclaration ReadDeclaration(JsonElement rows, Ue3ObjectIdentityCatalog identities)
    {
        Require(rows.GetArrayLength() == 2, "流式数组必须恰好提供数组及 inner 两份声明 serial。");

        var selection = identities.CaptureSelection().ToDictionary(x => x.PhysicalPackage, StringComparer.Ordinal);
        var fields = new Dictionary<long, (Ue3ExportIdentity Identity, Ue3ObjectSourceLocation Source, int TypeReference)>();

        foreach (var row in rows.EnumerateArray())
        {
            string package = row.GetProperty("physical_package").GetString()!;

            Require(selection.TryGetValue(package, out _), "流式声明物理包未显式选择。");

            var identity = identities.GetExport(selection[package].PackageId, row.GetProperty("export_index").GetInt32());
            var source = identities.GetExportSource(identity.PackageId, identity.ExportIndex);

            Require(row.GetProperty("package_sha256").GetString() == source.PackageSha256
                && source.SerialSha256 is not null && source.Bytes == 44,
                "流式声明缺少原始 schema serial 指纹或当前 758/2 布局不符。");

            byte[] serial = ReadSerial(row, source.LogicalOffset, source.Bytes, source.SerialSha256!);

            Require(ReadInt32(serial, 0) >= -1 && ReadInt32(serial, 0) == identity.SerializedNetIndex
                && Name(serial, 4) == new Ue3ObjectName("None", 0) && ReadInt32(serial, 16) == 1
                && (BinaryPrimitives.ReadUInt64LittleEndian(serial.AsSpan(20)) & 0x20) == 0,
                "流式属性声明 prefix、ArrayDim 或网络分支与已支持布局不符。");
            _ = identities.InspectReference(identity.PackageId, ReadInt32(serial, 12));
            _ = Name(serial, 28);
            _ = identities.InspectReference(identity.PackageId, ReadInt32(serial, 36));

            int type = ReadInt32(serial, 40);

            _ = identities.InspectReference(identity.PackageId, type);
            Require(fields.TryAdd(identity.ObjectId, (identity, source, type)), "流式属性声明身份重复。");

            Ue3ObjectName Name(byte[] value, int offset) => identities.ResolvePackageName(identity.PackageId,
                ReadInt32(value, offset), ReadInt32(value, offset + 4));
        }

        Ue3ExportIdentity definition = identities.ResolveUniqueLogicalPath(
            [new("Engine", 0), new("WorldInfo", 0), new("StreamingLevels", 0)]);

        Require(fields.TryGetValue(definition.ObjectId, out var array), "缺少真实 WorldInfo.StreamingLevels 属性声明。");
        RequireKind(array.Identity, "ArrayProperty");

        Ue3ClassIdentity owner = identities.GetClass(definition.PackageId, definition.OuterReference);

        Require(owner.Definition.LogicalPath.SequenceEqual([new Ue3ObjectName("Engine", 0), new("WorldInfo", 0)]),
            "StreamingLevels 声明属主不是已核实的 WorldInfo 类。");

        Ue3ExportIdentity? innerIdentity = identities.ResolveReference(definition.PackageId, array.TypeReference);

        Require(innerIdentity is not null && fields.TryGetValue(innerIdentity.ObjectId, out _), "数组的原始 inner 引用没有对应声明。");

        var inner = fields[innerIdentity!.ObjectId];

        RequireKind(inner.Identity, "ObjectProperty");
        Require(inner.Identity.PackageId == definition.PackageId && inner.Identity.OuterReference == definition.ExportIndex,
            "数组 inner 的真实 outer 不属于当前数组声明。");

        Ue3ExportIdentity? element = identities.ResolveReference(inner.Identity.PackageId, inner.TypeReference);

        Require(element is not null && element.LogicalPath.SequenceEqual([new Ue3ObjectName("Engine", 0), new("LevelStreaming", 0)]),
            "数组元素类不是原始 LevelStreaming 声明。");
        return new(array.Identity, array.Source, inner.Identity, inner.Source, identities.GetClass(element!.PackageId, element.ExportIndex));

        void RequireKind(Ue3ExportIdentity item, string name)
        {
            var kind = identities.InspectReference(item.PackageId, item.ClassReference);

            Require(kind.LogicalPath.SequenceEqual([new Ue3ObjectName("Core", 0), new(name, 0)]),
                "流式属性声明的真实元类型不符；不接受同名不同类型。");
        }
    }

    private static Dictionary<NativeMapObjectId, JsonElement> ReadWorldRows(JsonElement rows)
    {
        Require(rows.GetArrayLength() is >= 0 and <= MaximumWorlds, "流式 WorldInfo 来源数量超过预算。");

        var result = new Dictionary<NativeMapObjectId, JsonElement>();

        foreach (var row in rows.EnumerateArray())
        {
            var id = new NativeMapObjectId(row.GetProperty("physical_package").GetString()!,
                row.GetProperty("package_sha256").GetString()!, row.GetProperty("export_index").GetInt32());

            Require(result.TryAdd(id, row), "流式 WorldInfo 来源身份重复。");
        }

        return result;
    }

    private static byte[] ReadSerial(JsonElement row, int offset, int bytes, string expectedHash)
    {
        Require(bytes is > 0 and <= MaximumSerialBytes && row.GetProperty("logical_offset").GetInt32() == offset
            && row.GetProperty("bytes").GetInt32() == bytes
            && row.GetProperty("serial_sha256").GetString() == expectedHash, "增量 serial 的来源范围或指纹不符。");

        string encoded = row.GetProperty("serial_base64").GetString()!;

        Require(encoded.Length <= ((long)bytes + 2) / 3 * 4, "增量 serial 的 Base64 长度超过来源范围。");

        byte[] serial = Convert.FromBase64String(encoded);

        Require(serial.Length == bytes && Hash(serial) == expectedHash, "增量 serial 字节与原始来源不符。");
        return serial;
    }

    private static ReadOnlySpan<byte> ReadArrayPayload(byte[] serial, NativeMapExplicitProperty property)
    {
        int start = property.PayloadRelativeOffset;

        Require(start >= 24 && start <= serial.Length && property.PayloadBytes >= 4
            && property.PayloadBytes <= serial.Length - start, "流式数组 payload 越界或不含原始 count。");

        ReadOnlySpan<byte> header = serial.AsSpan(start - 24, 24);

        Require(ReadInt32(header, 0) == property.SerializedNameIndex && ReadInt32(header, 4) == 0
            && ReadInt32(header, 8) == property.SerializedTypeNameIndex && ReadInt32(header, 12) == 0
            && ReadInt32(header, 16) == property.PayloadBytes && ReadInt32(header, 20) == property.ArrayIndex,
            "流式数组原始 tag 的名称、类型、长度或静态槽位与地图投影不符。");

        ReadOnlySpan<byte> payload = serial.AsSpan(start, property.PayloadBytes);

        Require(Hash(payload) == property.PayloadSha256, "流式数组 payload 与地图属性来源不符。");
        return payload;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void NoDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "流式证据 JSON 含重复字段。");
                NoDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) NoDuplicateKeys(item);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
