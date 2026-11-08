using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// Resource identity from physical map package, BodySetup export, and actual ConvexElems index, not
/// world-Actor identity.
/// </summary>
public sealed record NativeCollisionResourceId(string PhysicalPackage, int BodyExportIndex, int ShapeOrdinal);

/// <summary>
/// Retains complete validated BodySetup sources and all explicit convex shapes, granting no world
/// activation.
/// </summary>
public sealed record NativeCollisionBodySource(NativeMapObjectId Id, string ObjectPath, NativeMapSourceRange Source,
    NativeMapSourceRange NativeTail, ImmutableArray<string> MissingShapeArrays,
    ImmutableArray<NativeConvexCollisionResource> Resources);

/// <summary>
/// Raw local geometry from dedicated fingerprint-pinned data. Instantiation still needs caller-provided
/// effective world transforms/collision decisions. Some source FaceTriData fail strict convex checks;
/// CreateCollider rejects them. Never silently discard shapes and claim a complete map.
/// </summary>
public sealed class NativeConvexCollisionResource
{
    internal NativeConvexCollisionResource(NativeCollisionResourceId id, string path, string sourceSha256,
        ImmutableArray<Vector3> vertices, ImmutableArray<int> triangleIndices)
    { Id = id; ObjectPath = path; SourceSha256 = sourceSha256; Vertices = vertices; TriangleIndices = triangleIndices; }

    public NativeCollisionResourceId Id { get; }
    public string ObjectPath { get; }
    public string SourceSha256 { get; }
    public ImmutableArray<Vector3> Vertices { get; }
    public ImmutableArray<int> TriangleIndices { get; }
    public NativeConvexCollider CreateCollider(Matrix4x4 verifiedLocalToWorld) => new(Vertices, TriangleIndices, verifiedLocalToWorld);
}

/// <summary>
/// Reads only collision-candidates.json in a specified absolute directory, without client search or
/// converters. Callers pin manifest SHA256; cross-check embedded source bytes, shape ranges,
/// vertex/triangle arrays, and projections per resource. A local resource catalog, not automatic world
/// construction from incomplete defaults, transforms, or streaming state.
/// </summary>
public sealed class NativeCollisionResourceCatalog
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumShapes = 100_000;
    private NativeCollisionResourceCatalog(string selectedMap, string manifestSha256, string sourceMapSha256,
        ImmutableArray<NativeConvexCollisionResource> resources, ImmutableArray<NativeCollisionBodySource> bodies)
    {
        SelectedPhysicalMap = selectedMap;
        ManifestSha256 = manifestSha256;
        SourceMapCandidatesSha256 = sourceMapSha256;
        Resources = resources;
        Bodies = bodies;
    }

    /// <summary>Physical main-map path used for collision extraction.</summary>
    public string SelectedPhysicalMap { get; }

    /// <summary>Manifest fingerprint pinned out of band by the caller during loading.</summary>
    public string ManifestSha256 { get; }

    /// <summary>
    /// Map-candidate content fingerprint underlying extraction, required to match exactly when binding
    /// instances.
    /// </summary>
    public string SourceMapCandidatesSha256 { get; }

    /// <summary>
    /// Complete source inventory. BodySetup/convex shapes remain present even when local geometry is
    /// unsupported.
    /// </summary>
    public ImmutableArray<NativeCollisionBodySource> Bodies { get; }

    /// <summary>
    /// All extracted explicit local convex shapes, preserving the existing resource-query interface.
    /// </summary>
    public ImmutableArray<NativeConvexCollisionResource> Resources { get; }

    public static NativeCollisionResourceCatalog Load(string absoluteDirectory, string trustedManifestSha256)
    {
        try { return LoadCore(absoluteDirectory, trustedManifestSha256); }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new InvalidDataException("碰撞资源格式或范围无效。", error); }
    }

    private static NativeCollisionResourceCatalog LoadCore(string directory, string trustedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Require(Path.IsPathFullyQualified(directory), "碰撞资源必须从显式绝对目录加载。");
        Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "碰撞资源目录不能为重解析点。");
        ValidateDigest(trustedHash);

        byte[] manifestBytes = Read(directory, "manifest.json", 64 * 1024);

        Require(Digest(manifestBytes) == trustedHash, "碰撞 manifest 不符合调用方固定指纹。");

        using var manifestDocument = Parse(manifestBytes);
        JsonElement manifest = manifestDocument.RootElement;

        Require(Integer(manifest, "schema_version", 1, 1) == 1 && Boolean(manifest, "candidate_only") &&
            !Boolean(manifest, "world_collision_complete") && Text(manifest, "file") == "collision-candidates.json",
            "碰撞 manifest 错误声明了完整世界能力或使用未知格式。");

        byte[] bytes = Read(directory, "collision-candidates.json", MaximumBytes);

        Require(bytes.Length == Integer(manifest, "bytes", 1, MaximumBytes) && Digest(bytes) == Hash(manifest, "sha256"),
            "碰撞产物长度或指纹不一致。");

        using var document = Parse(bytes);
        JsonElement data = document.RootElement;

        Require(Integer(data, "schema_version", 1, 1) == 1 && Boolean(data, "candidate_only") &&
            !Boolean(data, "client_files_required_at_runtime") && !Boolean(data, "world_collision_complete") &&
            !Boolean(data, "map_backend_complete") && Text(data, "verified_binary_sha256") ==
            "b4273f2658ca94eebc559a997fdfcd02d51e77ce75b892250c1db7fb80c70b51", "不支持的碰撞资源能力或二进制来源。");

        var resources = ImmutableArray.CreateBuilder<NativeConvexCollisionResource>();
        var bodySources = ImmutableArray.CreateBuilder<NativeCollisionBodySource>();
        var bodies = Array(data, "bodies", 1, MaximumShapes);
        var bodyIdentities = new HashSet<(string, int)>();
        var inventory = new Dictionary<(string, int), JsonElement>();

        foreach (var entry in Array(data, "source_body_inventory", bodies.GetArrayLength(), bodies.GetArrayLength()).EnumerateArray())
            Require(inventory.TryAdd((Text(entry, "package"), Integer(entry, "export_index", 1, 1_000_000)), entry),
                "源 BodySetup 清单含重复身份。");

        int totalVertices = 0, totalTriangles = 0;
        var packages = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var packageHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var packageRow in Array(data, "packages", 1, 256).EnumerateArray())
        {
            string physical = Text(packageRow, "package");

            Require(!Path.IsPathRooted(physical) && !physical.Contains('\\')
                && !physical.Split('/').Any(x => x is "" or "." or ".."), "碰撞物理包路径无效。");
            Integer(packageRow, "source_bytes", 1, int.MaxValue);
            Require(packageHashes.TryAdd(physical, Hash(packageRow, "source_sha256")), "碰撞物理包版本重复。");

            var names = Array(packageRow, "names", 1, 100_000).EnumerateArray().Select(name =>
            {
                Require(name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 and <= 4096 }, "碰撞包名称表无效。");
                return name.GetString()!;
            }).ToArray();

            Require(packages.TryAdd(physical, names), "碰撞包身份重复。");
        }

        string selectedMap = Text(data, "selected_map");

        Require(packages.ContainsKey(selectedMap), "碰撞来源没有选定主图。");

        string sourceMapSha256 = Hash(data, "source_map_candidates_sha256");

        foreach (JsonElement body in bodies.EnumerateArray())
        {
            string package = Text(body, "package"), path = Text(body, "object_path");
            int index = Integer(body, "export_index", 1, 1_000_000);

            Require(packages.TryGetValue(package, out string[]? packageNames) && bodyIdentities.Add((package, index)), "碰撞资源包缺失或 BodySetup 身份重复。");

            string hex = Text(body, "source_serial_hex", MaximumBytes * 2);

            Require(hex.Length % 2 == 0 && hex.Length <= MaximumBytes * 2, "碰撞源字节长度无效。");

            byte[] serial = Convert.FromHexString(hex);
            JsonElement source = body.GetProperty("source");

            Require(inventory.TryGetValue((package, index), out var inventoryEntry) && Text(inventoryEntry, "object_path") == path,
                "BodySetup 身份与源对象清单不符。");

            JsonElement inventorySource = inventoryEntry.GetProperty("source");

            Require(Integer(source, "bytes", 1, MaximumBytes) == Integer(inventorySource, "bytes", 1, MaximumBytes) &&
                Integer(source, "logical_offset", 0, int.MaxValue) == Integer(inventorySource, "logical_offset", 0, int.MaxValue) &&
                Hash(source, "sha256") == Hash(inventorySource, "sha256"), "BodySetup 来源范围与源清单不符。");
            Require(serial.Length == Integer(source, "bytes", 1, MaximumBytes) && Digest(serial) == Hash(source, "sha256"),
                "BodySetup 源字节指纹不符。");
            Integer(source, "logical_offset", 0, int.MaxValue);

            JsonElement decoded = body.GetProperty("decoded");
            JsonElement nativeTail = decoded.GetProperty("native_tail");
            int tailOffset = Integer(nativeTail, "relative_offset", 0, serial.Length);
            int tailBytes = Integer(nativeTail, "bytes", 0, serial.Length - tailOffset);

            Require(Text(nativeTail, "status") == "unsupported" && tailOffset + tailBytes == serial.Length
                && Digest(serial.AsSpan(tailOffset, tailBytes)) == Hash(nativeTail, "sha256"), "BodySetup 未解析尾部来源不符。");

            var missingArrays = Array(decoded, "missing_shape_arrays_not_defaulted", 0, 3).EnumerateArray()
                .Select(x => x.GetString() ?? throw new InvalidDataException("缺失形状数组名称无效。"))
                .ToImmutableArray();

            Require(missingArrays.SequenceEqual(new[] { "SphereElems", "BoxElems", "SphylElems" }),
                "当前碰撞格式只验证了显式 ConvexElems，不能改写其他形状的缺失状态。");

            int firstResource = resources.Count;
            var shapes = Array(decoded, "shapes", 0, MaximumShapes);
            var shapeOrdinals = new HashSet<int>();
            JsonElement convexArray = decoded.GetProperty("tags").GetProperty("AggGeom").GetProperty("fields").GetProperty("ConvexElems");
            var items = Array(convexArray, "items", 0, MaximumShapes);
            JsonElement aggregateSource = decoded.GetProperty("tags").GetProperty("AggGeom").GetProperty("source");

            Require(decoded.GetProperty("tags").GetProperty("AggGeom").GetProperty("fields").EnumerateObject()
                .Select(x => x.Name).SequenceEqual(new[] { "ConvexElems" }), "存在尚未验证的显式非凸形状数组。");

            int aggregateOffset = Integer(aggregateSource, "relative_offset", 0, serial.Length);
            int aggregateBytes = Integer(aggregateSource, "bytes", 1, serial.Length - aggregateOffset);

            Require(Digest(serial.AsSpan(aggregateOffset, aggregateBytes)) == Hash(aggregateSource, "sha256"), "AggGeom 源字节不符。");
            VerifyTag(decoded.GetProperty("tags").GetProperty("AggGeom"), "AggGeom", "StructProperty", 0, serial.Length, "KAggregateGeom");

            JsonElement arraySource = convexArray.GetProperty("source");
            int arrayOffset = Integer(arraySource, "relative_offset", aggregateOffset, aggregateOffset + aggregateBytes);
            int arrayBytes = Integer(arraySource, "bytes", 4, aggregateOffset + aggregateBytes - arrayOffset);

            Require(Digest(serial.AsSpan(arrayOffset, arrayBytes)) == Hash(arraySource, "sha256"), "ConvexElems 源字节不符。");
            VerifyTag(convexArray, "ConvexElems", "ArrayProperty", aggregateOffset, aggregateOffset + aggregateBytes);

            int serializedCount = BinaryPrimitives.ReadInt32LittleEndian(serial.AsSpan(arrayOffset, 4));

            Require(serializedCount == items.GetArrayLength() && Integer(convexArray, "count", 0, MaximumShapes) == items.GetArrayLength() &&
                shapes.GetArrayLength() == items.GetArrayLength(),
                "本版本只支持完整显式 ConvexElems 投影。");

            int nextItemOffset = arrayOffset + 4;

            foreach (JsonElement arrayItem in items.EnumerateArray())
            {
                JsonElement range = arrayItem.GetProperty("source");
                int start = Integer(range, "relative_offset", nextItemOffset, nextItemOffset);
                int length = Integer(range, "bytes", 1, arrayOffset + arrayBytes - start);

                Require(Digest(serial.AsSpan(start, length)) == Hash(range, "sha256"), "ConvexElems 子元素源字节不符。");
                nextItemOffset = checked(start + length);
            }
            Require(nextItemOffset == arrayOffset + arrayBytes, "ConvexElems 子元素未按原始顺序完整覆盖数组。");
            foreach (JsonElement shape in shapes.EnumerateArray())
            {
                int ordinal = Integer(shape, "ordinal", 0, MaximumShapes - 1);

                Require(ordinal < items.GetArrayLength() && shapeOrdinals.Add(ordinal) && Text(shape, "kind") == "convex" &&
                    Text(shape, "array") == "ConvexElems" && Text(shape, "coordinate_space") == "body_setup_resource_local" &&
                    Text(shape, "topology_source") == "explicit_serialized_FaceTriData" &&
                    Text(shape, "geometry_status") == "explicit_vertices_and_triangles" && Text(shape, "runtime_status") == "not_instantiated" &&
                    shape.GetProperty("world_transform").ValueKind == JsonValueKind.Null &&
                    shape.GetProperty("world_collision_enabled").ValueKind == JsonValueKind.Null,
                    "碰撞资源形状类型、索引或世界状态声明不受支持。");

                JsonElement item = items[ordinal], itemSource = item.GetProperty("source");
                int itemOffset = Integer(itemSource, "relative_offset", 0, serial.Length);
                int itemBytes = Integer(itemSource, "bytes", 1, serial.Length - itemOffset);

                Require(Digest(serial.AsSpan(itemOffset, itemBytes)) == Hash(itemSource, "sha256") &&
                    SameSource(shape.GetProperty("source"), itemSource), "形状范围或源字节指纹不符。");

                JsonElement fields = item.GetProperty("fields");

                VerifyTag(fields.GetProperty("VertexData"), "VertexData", "ArrayProperty", itemOffset, itemOffset + itemBytes);
                VerifyTag(fields.GetProperty("FaceTriData"), "FaceTriData", "ArrayProperty", itemOffset, itemOffset + itemBytes);

                var vertexSource = fields.GetProperty("VertexData").GetProperty("source");
                var triangleSource = fields.GetProperty("FaceTriData").GetProperty("source");
                ReadOnlySpan<byte> vertexBytes = Slice(vertexSource);
                ReadOnlySpan<byte> triangleBytes = Slice(triangleSource);

                Require(vertexBytes.Length >= 4 && triangleBytes.Length >= 4, "几何数组缺少数量前缀。");

                int vertexCount = BinaryPrimitives.ReadInt32LittleEndian(vertexBytes);
                int indexCount = BinaryPrimitives.ReadInt32LittleEndian(triangleBytes);

                Require(vertexCount is >= 4 and <= NativeConvexCollider.MaximumVertices &&
                    indexCount is >= 12 and <= NativeConvexCollider.MaximumTriangles * 3 && indexCount % 3 == 0 &&
                    vertexBytes.Length == 4 + vertexCount * 12 && triangleBytes.Length == 4 + indexCount * 4,
                    "几何数组数量或源字节范围无效。");

                var vertices = ImmutableArray.CreateBuilder<Vector3>(vertexCount);
                var indices = ImmutableArray.CreateBuilder<int>(indexCount);
                var projectedVertices = Array(shape, "vertices", vertexCount, vertexCount);
                var projectedIndices = Array(shape, "triangle_indices", indexCount, indexCount);

                for (int i = 0; i < vertexCount; i++)
                {
                    JsonElement row = projectedVertices[i];

                    Require(row.ValueKind == JsonValueKind.Array && row.GetArrayLength() == 3, "顶点投影长度错误。");

                    var components = new float[3];

                    for (int k = 0; k < 3; k++)
                    {
                        int bits = BinaryPrimitives.ReadInt32LittleEndian(vertexBytes.Slice(4 + i * 12 + k * 4, 4));
                        float value = BitConverter.Int32BitsToSingle(bits);

                        Require(float.IsFinite(value) && row[k].ValueKind == JsonValueKind.Number &&
                            row[k].GetDouble() == (double)value && BitConverter.SingleToInt32Bits(row[k].GetSingle()) == bits,
                            "顶点投影与真实 binary32 源字节不符。");
                        components[k] = value;
                    }
                    vertices.Add(new(components[0], components[1], components[2]));
                }
                for (int i = 0; i < indexCount; i++)
                {
                    int value = BinaryPrimitives.ReadInt32LittleEndian(triangleBytes.Slice(4 + i * 4, 4));

                    Require(value >= 0 && value < vertexCount && projectedIndices[i].ValueKind == JsonValueKind.Number &&
                        projectedIndices[i].GetInt32() == value, "三角形索引越界或与源字节不符。");
                    indices.Add(value);
                }
                resources.Add(new(new(package, index, ordinal), path, Hash(itemSource, "sha256"), vertices.MoveToImmutable(), indices.MoveToImmutable()));
                Require(resources.Count <= MaximumShapes, "碰撞资源数量超限。");
                totalVertices = checked(totalVertices + vertexCount); totalTriangles = checked(totalTriangles + indexCount / 3);

                ReadOnlySpan<byte> Slice(JsonElement range)
                {
                    int offset = Integer(range, "relative_offset", itemOffset, itemOffset + itemBytes);
                    int length = Integer(range, "bytes", 1, itemOffset + itemBytes - offset);
                    var slice = serial.AsSpan(offset, length);

                    Require(Digest(slice) == Hash(range, "sha256"), "几何数组源字节指纹错误。");
                    return slice;
                }
            }

            bodySources.Add(new(new(package, packageHashes[package], index), path,
                new(source.GetProperty("logical_offset").GetInt32(), serial.Length, Hash(source, "sha256")),
                new(checked(source.GetProperty("logical_offset").GetInt32() + tailOffset), tailBytes, Hash(nativeTail, "sha256")),
                missingArrays, Enumerable.Range(firstResource, resources.Count - firstResource)
                    .Select(resourceIndex => resources[resourceIndex]).ToImmutableArray()));

            void VerifyTag(JsonElement field, string expectedName, string expectedType, int minimum, int end, string? structName = null)
            {
                JsonElement tagSource = field.GetProperty("tag").GetProperty("source"), valueSource = field.GetProperty("source");
                int offset = Integer(tagSource, "relative_offset", minimum, end);
                int headerSize = structName is null ? 24 : 32;
                int size = Integer(tagSource, "bytes", headerSize, end - offset);
                ReadOnlySpan<byte> tag = serial.AsSpan(offset, size);

                Require(Digest(tag) == Hash(tagSource, "sha256") && NameAt(0, expectedName) && NameAt(8, expectedType) &&
                    BinaryPrimitives.ReadInt32LittleEndian(tag.Slice(16, 4)) == size - headerSize &&
                    BinaryPrimitives.ReadInt32LittleEndian(tag.Slice(20, 4)) == 0 &&
                    Integer(valueSource, "relative_offset", offset + headerSize, offset + headerSize) == offset + headerSize &&
                    Integer(valueSource, "bytes", size - headerSize, size - headerSize) == size - headerSize,
                    "几何源字段的原始 tag 名称、类型、数组下标或边界不符。");
                if (structName is not null) Require(NameAt(24, structName), "AggGeom 原始结构类型不符。");

                bool NameAt(int relative, string expected)
                {
                    int nameIndex = BinaryPrimitives.ReadInt32LittleEndian(serial.AsSpan(offset + relative, 4));

                    return nameIndex >= 0 && nameIndex < packageNames!.Length && packageNames[nameIndex] == expected &&
                        BinaryPrimitives.ReadInt32LittleEndian(serial.AsSpan(offset + relative + 4, 4)) == 0;
                }
            }
        }
        foreach (JsonElement statistics in new[] { manifest.GetProperty("statistics"), data.GetProperty("statistics") })
            Require(Integer(statistics, "bodies", 0, MaximumShapes) == bodies.GetArrayLength() &&
                Integer(statistics, "shapes", 0, MaximumShapes) == resources.Count &&
                Integer(statistics, "convex_vertices", 0, int.MaxValue) == totalVertices &&
                Integer(statistics, "convex_triangles", 0, int.MaxValue) == totalTriangles &&
                Integer(statistics.GetProperty("shape_kinds"), "convex", 0, MaximumShapes) == resources.Count,
                "碰撞资源统计不符。");
        Require(bodySources.Select(x => x.Id.PhysicalPackage).ToHashSet(StringComparer.Ordinal).SetEquals(packages.Keys),
            "碰撞包来源清单含没有 BodySetup 的额外记录。");
        return new(selectedMap, trustedHash, sourceMapSha256, resources.ToImmutable(), bodySources.ToImmutable());
    }

    private static bool SameSource(JsonElement a, JsonElement b) =>
        a.GetProperty("relative_offset").GetInt32() == b.GetProperty("relative_offset").GetInt32() &&
        a.GetProperty("bytes").GetInt32() == b.GetProperty("bytes").GetInt32() && Hash(a, "sha256") == Hash(b, "sha256");
    private static byte[] Read(string directory, string name, int maximum)
    {
        string path = Path.Combine(directory, name);

        Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "碰撞数据文件不能为重解析点。");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Require(stream.Length > 0 && stream.Length <= maximum, "碰撞数据文件大小超限。");

        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "读取期间碰撞数据大小发生变化。");
        return bytes;
    }
    private static JsonDocument Parse(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });

        try { NoDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void NoDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonProperty property in value.EnumerateObject())
            { Require(names.Add(property.Name), "碰撞数据含重复 JSON 属性。"); NoDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) NoDuplicates(item);
    }
    private static JsonElement Array(JsonElement row, string name, int minimum, int maximum)
    {
        var value = row.GetProperty(name);

        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= minimum && value.GetArrayLength() <= maximum,
            $"碰撞数组 {name} 类型或数量错误。"); return value;
    }
    private static int Integer(JsonElement row, string name, int minimum, int maximum)
    {
        var value = row.GetProperty(name);

        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int _) &&
            value.GetInt32() >= minimum && value.GetInt32() <= maximum, $"碰撞整数 {name} 类型或范围错误。");
        return value.GetInt32();
    }
    private static bool Boolean(JsonElement row, string name)
    {
        var value = row.GetProperty(name);

        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"碰撞布尔 {name} 类型错误。"); return value.GetBoolean();
    }
    private static string Text(JsonElement row, string name, int maximum = 4096)
    {
        var value = row.GetProperty(name);

        Require(value.ValueKind == JsonValueKind.String && value.GetString() is string text && text.Length > 0 &&
            text.Length <= maximum && !text.Contains('\0'), $"碰撞文本 {name} 类型或长度错误。"); return value.GetString()!;
    }
    private static string Hash(JsonElement row, string name) { string value = Text(row, name, 64); ValidateDigest(value); return value; }
    private static void ValidateDigest(string value) => Require(value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "碰撞 SHA256 格式错误。");
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
