using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AFServer.AssaultFire.Matches.Protocol;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// Independent loader for development-extracted JSON only. Trust anchors are three caller-pinned manifest
/// SHA256 values; physical-package selection is explicit. Reads no client resources and registers no
/// candidate data in production manifests.
/// </summary>
public static class Ue3ObjectIdentityLoader
{
    public const string VerifiedBinarySha256 = "b4273f2658ca94eebc559a997fdfcd02d51e77ce75b892250c1db7fb80c70b51";
    private const int MaxJsonBytes = 64 * 1024 * 1024;
    private const int MaxRecords = 500_000;
    private sealed record ExportRow(int Class, int Super, int Outer, Ue3ObjectName Name, int Archetype,
        ulong Flags, int SerialBytes, int SerialOffset);

    public static Ue3ObjectIdentityCatalog LoadResearchSelection(string researchRoot, Ue3ObjectSourceTrust trust,
        IReadOnlyList<Ue3PhysicalPackageSelection> selected)
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(selected);
        try { return Load(researchRoot, trust, selected); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or OverflowException or ArgumentOutOfRangeException)
        { throw new InvalidDataException("对象身份数据格式或范围无效。", error); }
    }

    private static Ue3ObjectIdentityCatalog Load(string researchRoot, Ue3ObjectSourceTrust trust,
        IReadOnlyList<Ue3PhysicalPackageSelection> selected)
    {
        Require(selected.Count is > 0 and <= 256, "物理包选择数量无效。");

        HashSet<long> ids = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (var item in selected)
            Require(item is not null && item.PackageId is > 0 and <= int.MaxValue && ids.Add(item.PackageId)
                && !string.IsNullOrWhiteSpace(item.PhysicalPackage) && names.Add(item.PhysicalPackage), "物理包身份无效或重复。");

        string packageRoot = Path.Combine(researchRoot, "package-data");
        string schemaRoot = Path.Combine(researchRoot, "actor-schema");
        string defaultsRoot = Path.Combine(researchRoot, "class-defaults");
        using var packageManifest = ReadPinned(packageRoot, "candidate-manifest.json", trust.PackageManifestSha256, 1024 * 1024);
        using var schemaManifest = ReadPinned(schemaRoot, "manifest.json", trust.ActorSchemaManifestSha256, 1024 * 1024);
        using var defaultManifest = ReadPinned(defaultsRoot, "manifest.json", trust.ClassDefaultsManifestSha256, 1024 * 1024);

        Candidate(packageManifest.RootElement);
        Require(Text(packageManifest.RootElement, "kind") == "package_candidate_manifest", "不是受支持的包候选 manifest。");
        Require(!Bool(packageManifest.RootElement, "client_files_required_at_runtime"), "产物声明依赖客户端文件。");
        SchemaCandidate(schemaManifest.RootElement);
        DefaultsCandidate(defaultManifest.RootElement);
        Require(Text(defaultManifest.RootElement, "actor_schema_manifest_sha256") == trust.ActorSchemaManifestSha256,
            "defaults 和 actor-schema 不属于同一提取集合。");

        var fileEntries = packageManifest.RootElement.GetProperty("files");

        Require(fileEntries.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["package-index.json", "script-object-directory.json"]), "包 manifest 文件集合不受支持。");

        using var index = ReadEntry(packageRoot, "package-index.json", fileEntries.GetProperty("package-index.json"));
        using var directory = ReadEntry(packageRoot, "script-object-directory.json", fileEntries.GetProperty("script-object-directory.json"));

        Candidate(index.RootElement);
        Candidate(directory.RootElement);
        Require(Text(index.RootElement, "kind") == "verified_package_summary_inventory"
            && Text(directory.RootElement, "kind") == "validated_package_object_directory"
            && !Bool(index.RootElement, "client_files_required_at_runtime")
            && !Bool(directory.RootElement, "client_files_required_at_runtime"), "目录格式标识或客户端依赖声明无效。");
        ValidateFormats(directory.RootElement.GetProperty("record_formats"));

        var summaries = MapPackages(index.RootElement);
        var directories = MapPackages(directory.RootElement);
        var schemas = MapPackages(schemaManifest.RootElement);
        var defaults = MapPackages(defaultManifest.RootElement);
        var result = new List<Ue3ObjectIdentityCatalog.PackageData>();
        int totalExports = 0;

        foreach (var choice in selected)
        {
            Require(summaries.ContainsKey(choice.PhysicalPackage) && directories.ContainsKey(choice.PhysicalPackage)
                && schemas.ContainsKey(choice.PhysicalPackage) && defaults.ContainsKey(choice.PhysicalPackage),
                $"选定包 {choice.PhysicalPackage} 缺少完整目录/schema/CDO 证据。");

            var schemaEntry = schemas[choice.PhysicalPackage];
            var defaultsEntry = defaults[choice.PhysicalPackage];
            using var schema = ReadEntry(schemaRoot, Text(schemaEntry, "file"), schemaEntry);
            using var defaultData = ReadEntry(defaultsRoot, Text(defaultsEntry, "file"), defaultsEntry);

            SchemaCandidate(schema.RootElement);
            DefaultsCandidate(defaultData.RootElement);
            Require(Text(defaultData.RootElement, "actor_schema_sha256") == Text(schemaEntry, "sha256"), "逐包 schema/defaults 指纹不一致。");

            var package = ParsePackage(choice, summaries[choice.PhysicalPackage], directories[choice.PhysicalPackage],
                schema.RootElement, defaultData.RootElement);

            totalExports = checked(totalExports + package.Exports.Length);
            Require(totalExports <= MaxRecords, "选择集合的 export 数量超限。");
            result.Add(package);
        }
        return new Ue3ObjectIdentityCatalog(result);
    }

    private static Ue3ObjectIdentityCatalog.PackageData ParsePackage(Ue3PhysicalPackageSelection selection,
        JsonElement summary, JsonElement directory, JsonElement schema, JsonElement defaults)
    {
        Require(Int(summary, "version") == 758 && Int(summary, "licensee_version") == 2
            && Int(schema, "package_version") == 758 && Int(schema, "licensee_version") == 2, "包版本不支持。");
        Require(Text(schema, "package") == selection.PhysicalPackage && Text(defaults, "package") == selection.PhysicalPackage,
            "逐包产物物理身份不符。");

        string source = Text(directory, "source_sha256");

        HexHash(source);
        Require(source == Text(schema, "package_sha256") && source == Text(defaults, "package_sha256")
            && Text(summary, "decoded_summary_sha256") == Text(schema, "decoded_summary_sha256")
            && Text(summary, "decoded_summary_sha256") == Text(defaults, "decoded_summary_sha256"), "同名物理包的源指纹不一致。");

        int exportCount = Int(summary, "export_count");
        int importCount = Int(summary, "import_count");

        Require(exportCount is > 0 and <= MaxRecords && importCount is >= 0 and <= MaxRecords, "包目录数量超限。");

        var nameRows = directory.GetProperty("names");

        Require(nameRows.GetArrayLength() == Int(summary, "name_count") && nameRows.GetArrayLength() is > 0 and <= MaxRecords,
            "名称表数量不一致或超限。");

        string[] nameTable = new string[nameRows.GetArrayLength()];
        int n = 0;

        foreach (var row in nameRows.EnumerateArray())
        {
            Require(row.GetArrayLength() == 2, "FName 表列数错误。");

            string text = row[0].GetString()!;

            Require(!string.IsNullOrEmpty(text) && text.Length <= 65536 && !text.Contains('\0'), "FName 文本长度或终止符错误。");
            _ = HexU64(row[1].GetString()!);
            nameTable[n++] = text;
        }
        Ue3ObjectName Name(int index, int number)
        {
            Require(index >= 0 && index < nameTable.Length && number >= 0, "FName 索引或 number 错误。");
            return new(nameTable[index], number);
        }
        void Ref(int index) => Require(index >= -importCount && index <= exportCount, "对象引用越界。");

        var importRows = directory.GetProperty("imports");
        var exportRows = directory.GetProperty("exports");

        Require(importRows.GetArrayLength() == importCount && exportRows.GetArrayLength() == exportCount, "导入/导出表数量不一致。");

        var imports = new (int Outer, Ue3ObjectName Name, Ue3ObjectName ClassPackage, Ue3ObjectName Class)[importCount];

        for (int i = 0; i < importCount; i++)
        {
            var row = importRows[i];

            Require(row.GetArrayLength() == 7, "import 列数错误。");

            int outer = row[4].GetInt32(); Ref(outer);
            imports[i] = (outer, Name(row[5].GetInt32(), row[6].GetInt32()),
                Name(row[0].GetInt32(), row[1].GetInt32()), Name(row[2].GetInt32(), row[3].GetInt32()));
        }

        var exports = new ExportRow[exportCount];
        int header = Int(summary, "total_header_size");
        int logicalSize = Int(summary, "logical_size");

        Require(header >= 0 && logicalSize >= header, "包逻辑范围错误。");
        for (int i = 0; i < exportCount; i++)
        {
            var row = exportRows[i];

            Require(row.GetArrayLength() == 13, "export 列数错误。");

            int @class = row[0].GetInt32(), super = row[1].GetInt32(), outer = row[2].GetInt32(), archetype = row[5].GetInt32();

            Ref(@class); Ref(super); Ref(outer); Ref(archetype);

            int size = row[7].GetInt32(), offset = row[8].GetInt32();

            Require(size > 0 && offset >= header && (long)offset + size <= logicalSize, "export serial 超出包范围。");
            exports[i] = new(@class, super, outer, Name(row[3].GetInt32(), row[4].GetInt32()), archetype,
                HexU64(row[6].GetString()!), size, offset);
            _ = row[9].GetUInt32(); _ = row[12].GetUInt32();
            Require(row[10].GetArrayLength() <= 4096, "export generation 数量超限。");
            foreach (var count in row[10].EnumerateArray()) _ = count.GetUInt32();
            _ = Guid(row[11]);
        }

        var pathCache = new Dictionary<int, ImmutableArray<Ue3ObjectName>>();
        var rootName = new Ue3ObjectName(Path.GetFileNameWithoutExtension(selection.PhysicalPackage), 0);

        ImmutableArray<Ue3ObjectName> PathFor(int index, HashSet<int>? seen = null)
        {
            Ref(index); Require(index != 0, "零引用不能获得对象身份路径。");
            if (pathCache.TryGetValue(index, out var cached)) return cached;
            seen ??= [];
            Require(seen.Count < 128 && seen.Add(index), "对象 outer/class 路径循环或过深。");

            int outer = index > 0 ? exports[index - 1].Outer : imports[-index - 1].Outer;
            var name = index > 0 ? exports[index - 1].Name : imports[-index - 1].Name;
            ImmutableArray<Ue3ObjectName> prefix = [];

            if (outer != 0) prefix = PathFor(outer, seen);
            else if (index > 0)
            {
                int classIndex = exports[index - 1].Class;
                bool isPackage = false;

                if (classIndex != 0)
                {
                    var typePath = PathFor(classIndex, seen);

                    // Accept only observed Core.Class imports, preventing same-named Package strings from fabricating logical roots.
                    isPackage = classIndex < 0 && imports[-classIndex - 1].ClassPackage == new Ue3ObjectName("Core", 0)
                        && imports[-classIndex - 1].Class == new Ue3ObjectName("Class", 0)
                        && typePath.Length == 2 && typePath[0] == new Ue3ObjectName("Core", 0)
                        && typePath[1] == new Ue3ObjectName("Package", 0);
                }
                if (!isPackage) prefix = [rootName];
            }
            seen.Remove(index);

            var path = prefix.Add(name);

            pathCache.Add(index, path);
            return path;
        }

        var generationRows = summary.GetProperty("generations");

        Require(generationRows.GetArrayLength() is > 0 and <= 4096, "代际表数量错误。");

        var generations = generationRows.EnumerateArray().Select(x => Int(x, "net_object_count")).ToImmutableArray();

        Require(generations.All(x => x >= 0) && generations[^1] == Int(schema, "generation_net_object_count"), "代际范围不一致。");

        var metadata = new Dictionary<int, JsonElement>();
        var netIndices = new Dictionary<int, int>();

        foreach (var item in schema.GetProperty("objects").EnumerateArray())
        {
            int index = Int(item, "export_index");

            Require(index > 0 && index <= exportCount && metadata.TryAdd(index, item), "schema 对象身份重复或越界。");

            var row = exports[index - 1];

            SourceExtent(item.GetProperty("source"), row);

            var name = item.GetProperty("object_name");

            Require(name.GetArrayLength() == 3 && Name(name[0].GetInt32(), name[1].GetInt32()) == row.Name
                && name[2].GetString() == row.Name.Text && Int(item, "outer") == row.Outer, "schema 对象目录身份不符。");

            int netIndex = Int(item, "serialized_net_index"); Require(netIndex >= -1, "串行 NetIndex 无效。");
            netIndices.Add(index, netIndex);
        }

        var cdoByClass = new Dictionary<int, JsonElement>();

        foreach (var item in defaults.GetProperty("defaults").EnumerateArray())
        {
            if (Text(item, "kind") != "ClassDefaultObject") continue;

            int index = Int(item, "export_index"), classIndex = Int(item, "class_export_index");

            Require(index > 0 && index <= exportCount && classIndex > 0 && classIndex <= exportCount
                && cdoByClass.TryAdd(classIndex, item), "CDO/class 索引无效或重复。");

            var cdo = exports[index - 1]; var classRow = exports[classIndex - 1];

            Require((cdo.Flags & 0x200) != 0 && cdo.Flags == HexU64(Text(item, "object_flags_u64"))
                && cdo.Class == classIndex && classRow.Class == 0 && cdo.Name.Number == classRow.Name.Number
                && cdo.Name.Text == "Default__" + classRow.Name.Text && Int(item, "archetype_object_index") == cdo.Archetype,
                "CDO flag/name/class/archetype 双向身份不符。");
            Require(metadata.TryGetValue(classIndex, out var classMeta) && Text(classMeta, "kind") == "ZeroClassStructCandidate",
                "CDO 缺少对应类序列化证据。");
            Require(Int(classMeta, "super_export_table") == classRow.Super && Int(classMeta, "super_struct") == classRow.Super,
                "类 UStruct.Super 与 export super 不一致。");
            SourceExtent(item.GetProperty("source"), cdo);

            var finalRef = item.GetProperty("class_default_reference_source");

            Require(Int(finalRef, "bytes") == 4 && Int(finalRef, "logical_offset") == classRow.SerialOffset + classRow.SerialBytes - 4
                && Text(finalRef, "class_serial_sha256") == Text(classMeta.GetProperty("source"), "sha256"),
                "class 最后 CDO 引用的来源不匹配。");

            int netIndex = Int(item, "serialized_net_index"); Require(netIndex >= -1 && netIndices.TryAdd(index, netIndex), "CDO NetIndex 重复或无效。");
        }
        Require(exports.Count(x => (x.Flags & 0x200) != 0) == cdoByClass.Count
            && metadata.Values.Count(x => Text(x, "kind") == "ZeroClassStructCandidate") == cdoByClass.Count,
            "部分 CDO/类缺失双向证据。");

        var identities = ImmutableArray.CreateBuilder<Ue3ExportIdentity>(exportCount);
        var serialHashes = metadata.ToDictionary(x => x.Key, x => Text(x.Value.GetProperty("source"), "sha256"));

        foreach (var cdo in cdoByClass.Values)
            serialHashes.Add(Int(cdo, "export_index"), Text(cdo.GetProperty("source"), "sha256"));

        var sources = ImmutableArray.CreateBuilder<Ue3ObjectSourceLocation>(exportCount);

        for (int i = 1; i <= exportCount; i++)
        {
            var row = exports[i - 1];
            // This ID identifies server objects only. Network slots require a separately proven SerializeNetIndex branch.
            long objectId = checked((selection.PackageId << 32) | (uint)i);

            identities.Add(new(objectId, selection.PackageId, i, PathFor(i), row.Class, row.Super, row.Outer,
                row.Archetype, row.Flags, netIndices.TryGetValue(i, out var net) ? net : null));
            sources.Add(new(selection.PhysicalPackage, source, row.SerialOffset, row.SerialBytes,
                serialHashes.GetValueOrDefault(i)));
        }

        var package = new Ue3ObjectIdentityCatalog.PackageData
        {
            Selection = selection, SourceSha256 = source, Names = nameTable.ToImmutableArray(),
            Flags = summary.GetProperty("package_flags").GetUInt32(),
            Generations = generations, Guid = Guid(summary.GetProperty("guid_u32_words")), Exports = identities.MoveToImmutable(),
            Sources = sources.MoveToImmutable(),
            ImportPaths = Enumerable.Range(1, importCount).Select(i => PathFor(-i)).ToArray(),
            ImportTypes = imports.Select(x => (x.ClassPackage.Text, x.Class.Text, x.ClassPackage.Number, x.Class.Number)).ToArray()
        };

        foreach (var (classIndex, cdo) in cdoByClass)
        {
            var tags = cdo.GetProperty("tag_list");

            Require(Text(tags, "validation") == "complete_tag_list_extent" && Int(tags, "relative_offset") == 4,
                "CDO 显式 tag 区未完整解析。");

            var properties = tags.GetProperty("properties").EnumerateArray().ToArray();

            foreach (var property in properties)
            foreach (string key in new[] { "name", "type" })
            {
                var value = property.GetProperty(key);

                Require(value.GetArrayLength() == 3
                    && Name(value[0].GetInt32(), value[1].GetInt32()).Text == value[2].GetString(),
                    "CDO tag 的 FName 文本与物理包名称表不符。");
            }

            var identity = new Ue3ClassIdentity(package.Exports[classIndex - 1], package.Exports[Int(cdo, "export_index") - 1],
                properties.Select(x => x.Clone()).ToImmutableArray());

            package.Classes.Add(classIndex, identity);
            package.DefaultClasses.Add(identity.DefaultObject.ExportIndex, identity);
        }
        return package;
    }

    private static void SourceExtent(JsonElement source, ExportRow row)
    {
        Require(Int(source, "bytes") == row.SerialBytes && Int(source, "logical_offset") == row.SerialOffset, "源 serial 范围不一致。");
        HexHash(Text(source, "sha256"));
    }

    private static JsonDocument ReadPinned(string root, string file, string expectedHash, int maximum, int? expectedBytes = null)
    {
        HexHash(expectedHash);
        Require(file == Path.GetFileName(file) && !file.Contains('\\') && !file.Contains('/') && file is not "." and not "..",
            "manifest 文件名不能越过产物目录。");

        using var stream = new FileStream(Path.Combine(root, file), FileMode.Open, FileAccess.Read, FileShare.Read);

        Require(stream.Length > 0 && stream.Length <= maximum && (!expectedBytes.HasValue || stream.Length == expectedBytes),
            "对象身份 JSON 长度不符或超限。");

        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == expectedHash, "对象身份产物 SHA256 与信任锚不符。");

        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });

        try { NoDuplicateKeys(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static JsonDocument ReadEntry(string root, string file, JsonElement entry)
        => ReadPinned(root, file, Text(entry, "sha256"), MaxJsonBytes, Int(entry, "bytes"));
    private static Dictionary<string, JsonElement> MapPackages(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in root.GetProperty("packages").EnumerateArray())
            Require(result.Count < 4096 && result.TryAdd(Text(item, "package"), item), "包清单重复或超限。");
        return result;
    }
    private static void Candidate(JsonElement value)
        => Require(Int(value, "schema_version") == 1 && Bool(value, "candidate_only"), "不支持的候选 schema 或错误完成标志。");
    private static void SchemaCandidate(JsonElement value)
    {
        Candidate(value);
        Require(Text(value, "verified_binary_sha256") == VerifiedBinarySha256 && !Bool(value, "wire_schema_complete"), "schema 源版本/完成标志错误。");
    }
    private static void DefaultsCandidate(JsonElement value)
    {
        SchemaCandidate(value);
        Require(!Bool(value, "runtime_defaults_complete"), "不支持将显式默认值标记为完整运行时 defaults。");
    }
    private static void NoDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (var item in value.EnumerateObject())
            { Require(names.Add(item.Name), "JSON 对象含重复字段。"); NoDuplicateKeys(item.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) NoDuplicateKeys(item);
    }
    private static void ValidateFormats(JsonElement formats)
    {
        Require(TextList(formats.GetProperty("names")) == "text|flags_u64_hex"
            && TextList(formats.GetProperty("imports")) == "class_package_name_index|class_package_name_number|class_name_index|class_name_number|outer_object_index|object_name_index|object_name_number"
            && TextList(formats.GetProperty("exports")) == "class_object_index|super_object_index|outer_object_index|object_name_index|object_name_number|archetype_object_index|object_flags_u64_hex|serial_size|serial_offset|export_flags_u32|generation_net_object_counts|guid_u32_words|package_flags_u32",
            "对象目录列定义变化，不能沿用旧解释。");
    }
    private static string TextList(JsonElement array) => string.Join('|', array.EnumerateArray().Select(x => x.GetString()));
    private static Ue3PackageGuid Guid(JsonElement array)
    {
        Require(array.GetArrayLength() == 4, "GUID 不是四个 word。");
        return new(array[0].GetUInt32(), array[1].GetUInt32(), array[2].GetUInt32(), array[3].GetUInt32());
    }
    private static ulong HexU64(string value)
    { Require(value.Length == 16 && value.All(Uri.IsHexDigit), "u64 标志必须是 16 位十六进制。"); return ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture); }
    private static void HexHash(string value) => Require(value is not null && value.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "SHA256 必须是小写 64 位十六进制。");
    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static bool Bool(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException("字符串为 null。");
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}
