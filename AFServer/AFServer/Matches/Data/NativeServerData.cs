using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reads offline native data from the publish directory. Catalog records and raw configuration are not a
/// runnable world backend.
/// </summary>
public sealed class NativeServerData
{
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumCatalogBytes = 4 * 1024 * 1024;
    private const int MaximumRulesBytes = 32 * 1024 * 1024;
    private const int MaximumTextLength = 256 * 1024;
    private const int MaximumEntries = 200_000;
    private const int MaximumSourceLine = 32 * 1024 * 1024;
    private static readonly string[] FileNames = ["manifest.json", "map-catalog.json", "config-rules.json"];
    private static readonly ImmutableDictionary<string, string> SourcePurposes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DefaultGame.ini"] = "map_mode_directory",
        ["DefaultWeapon.ini"] = "weapon_rules",
        ["DefaultTGBioWeapon.ini"] = "weapon_rules",
        ["DefaultTGBio3Weapon.ini"] = "weapon_rules",
        ["DefaultTGTankWeapon.ini"] = "weapon_rules",
        ["DefaultMechaWeapon.ini"] = "weapon_rules",
        ["DefaultPZZombieWeapon.ini"] = "weapon_rules",
        ["DefaultSkill.ini"] = "skill_rules",
        ["DefaultTGBioSkill.ini"] = "skill_rules",
        ["DefaultPZZombieSkill.ini"] = "skill_rules",
        ["DefaultPVEGame.ini"] = "pve_rules",
        ["DefaultItemLibrary.ini"] = "item_definitions",
        ["DefaultCommodityLibrary.ini"] = "commodity_definitions"
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static readonly ImmutableDictionary<string, string> MissingCapabilities = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["world_backend"] = "当前程序没有可运行的原生世界后端。",
        ["map_geometry_and_collision"] = "发布数据尚无完整世界碰撞；研究用局部凸几何不等于已激活地图。",
        ["map_actors_navigation_and_kismet"] = "离线数据没有场景对象、出生点、导航或关卡脚本。",
        ["class_defaults_and_inheritance"] = "已有真实类身份和显式默认值读取，尚未合成有效默认值、原生构造和配置继承。",
        ["configuration_semantics"] = "仅保留配置重复键和操作符，尚未求值。",
        ["replicated_properties_and_rpc_schema"] = "已有字段/RPC编解码核心，生产对象、权限和复制调度尚未接通。",
        ["authoritative_gameplay_simulation"] = "已有有限权威模拟核心，尚未接通真实模式数据、完整物理和生产游戏协议。",
        ["map_availability"] = "地图目录记录不能证明对应地图可加载或可游玩。"
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static readonly ImmutableArray<string> Available = ["configuration_map_mode_directory", "raw_configuration_rules"];

    private NativeServerData(NativeServerDataSummary summary, ImmutableArray<NativeMapDefinition> maps,
        ImmutableArray<NativeModeDefinition> modes, ImmutableArray<NativeMapPrefix> prefixes,
        ImmutableArray<NativeRuleDocument> rules)
    {
        Summary = summary; Maps = maps; Modes = modes; Prefixes = prefixes; RuleDocuments = rules;
    }

    public NativeServerDataSummary Summary { get; }
    public ImmutableArray<NativeMapDefinition> Maps { get; }
    public ImmutableArray<NativeModeDefinition> Modes { get; }
    public ImmutableArray<NativeMapPrefix> Prefixes { get; }
    public ImmutableArray<NativeRuleDocument> RuleDocuments { get; }

    /// <summary>
    /// directory must be an absolute publish root; read files only from data/native beneath it. Reject link
    /// redirection, unlisted files, oversized content, hash mismatches, duplicate JSON properties, and
    /// unsupported structures. Never search game directories or the working directory, invoke exporters, or
    /// execute raw configuration code.
    /// </summary>
    public static NativeServerData Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("原生数据加载需要绝对发布目录。", nameof(directory));

        string root = Path.GetFullPath(directory);

        EnsureDirectory(root);
        EnsureDirectory(Path.Combine(root, "data"));

        string dataDirectory = Path.Combine(root, "data", "native");

        EnsureDirectory(dataDirectory);

        var entries = Directory.EnumerateFileSystemEntries(dataDirectory).Take(FileNames.Length + 1).ToArray();

        Require(entries.Length == FileNames.Length && entries.All(path => FileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal)),
            "原生数据目录必须且只能包含三个已知 JSON 文件。");

        byte[] manifestBytes = ReadBounded(dataDirectory, "manifest.json", MaximumManifestBytes);
        using JsonDocument manifestDocument = Parse(manifestBytes, "manifest.json");
        JsonElement manifest = manifestDocument.RootElement;

        Schema(manifest, "manifest.json");

        var files = Array(manifest, "files", 2, 2);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (JsonElement artifact in files.EnumerateArray())
        {
            string name = Text(artifact, "name", 64);

            Require(name is "map-catalog.json" or "config-rules.json" && !payloads.ContainsKey(name),
                "清单包含重复、未知或越界文件名。");

            int maximum = name == "map-catalog.json" ? MaximumCatalogBytes : MaximumRulesBytes;
            long expectedLength = Integer(artifact, "bytes", 1, maximum);
            string expectedHash = Digest(artifact, "sha256");
            byte[] bytes = ReadBounded(dataDirectory, name, maximum);

            Require(bytes.Length == expectedLength && Convert.ToHexStringLower(SHA256.HashData(bytes)) == expectedHash,
                $"{name} 的长度或 SHA-256 与清单不一致。");
            payloads.Add(name, bytes);
        }

        using JsonDocument catalogDocument = Parse(payloads["map-catalog.json"], "map-catalog.json");
        using JsonDocument rulesDocument = Parse(payloads["config-rules.json"], "config-rules.json");
        JsonElement catalog = catalogDocument.RootElement, rules = rulesDocument.RootElement;

        Schema(catalog, "map-catalog.json"); Schema(rules, "config-rules.json");
        Require(Text(catalog, "kind", 64) == "configuration_map_directory" && Text(catalog, "source", 64) == "DefaultGame.ini",
            "地图目录类型或来源无效。");
        Require(Text(rules, "kind", 64) == "unresolved_configuration_rules", "配置包错误声明了已解析的规则语义。");
        Text(rules, "value_format", 1024);

        var maps = Array(catalog, "maps", 1, 65536).EnumerateArray().Select(row => new NativeMapDefinition(
            checked((ushort)Integer(row, "map_id", 0, ushort.MaxValue)), Text(row, "map_name", 1024),
            Text(row, "display_name", 4096, true), Origin(row))).ToImmutableArray();
        var modes = Array(catalog, "modes", 1, 4096).EnumerateArray().Select(row => new NativeModeDefinition(
            checked((uint)Integer(row, "mode_id", 0, uint.MaxValue)), Text(row, "game_class", 1024), Origin(row))).ToImmutableArray();
        var prefixes = Array(catalog, "prefixes", 1, 4096).EnumerateArray().Select(row => new NativeMapPrefix(
            Text(row, "prefix", 256), Text(row, "game_class", 1024), Boolean(row, "uses_common_package"), Origin(row))).ToImmutableArray();
        var sources = ValidateSources(manifest);
        var documents = ValidateRules(rules, sources);
        int sectionCount = documents.Sum(document => document.Sections.Length);
        int assignmentCount = documents.Sum(document => document.Sections.Sum(section => section.Entries.Length));
        int unparsedCount = documents.Sum(document => document.Sections.Sum(section => section.UnparsedLines.Length));
        JsonElement statistics = Object(manifest, "statistics");
        var counts = new Dictionary<string, int>
        {
            ["source_files"] = sources.Count, ["maps"] = maps.Length, ["modes"] = modes.Length,
            ["prefixes"] = prefixes.Length, ["rule_documents"] = documents.Length,
            ["rule_sections"] = sectionCount, ["rule_assignments"] = assignmentCount, ["rule_unparsed_lines"] = unparsedCount
        };

        foreach (var (name, count) in counts)
            Require(Integer(statistics, name, 0, int.MaxValue) == count, $"清单统计 {name} 与实际内容不一致。");
        Require(Integer(statistics, "source_bytes", 1, long.MaxValue) == sources.Values.Sum(source => Integer(source, "source_bytes", 1, 8 * 1024 * 1024)) &&
            Integer(statistics, "decoded_bytes", 1, long.MaxValue) == sources.Values.Sum(source => Integer(source, "decoded_bytes", 1, MaximumSourceLine)),
            "清单来源字节数统计不一致。");

        JsonElement runtime = Object(manifest, "runtime");

        Require(!Boolean(runtime, "client_files_required") && !Boolean(runtime, "original_game_files_packaged"),
            "数据包声明了运行时客户端文件依赖或原始游戏文件，不符合独立数据格式。");

        bool claimedComplete = Boolean(runtime, "complete_game_server");
        JsonElement capabilities = Object(manifest, "capabilities");
        var available = Array(capabilities, "available", Available.Length, Available.Length).EnumerateArray()
            .Select(item => StringValue(item, 128)).ToImmutableArray();

        Require(available.Distinct(StringComparer.Ordinal).Count() == Available.Length && available.All(Available.Contains),
            "数据包声明了当前读取器无法提供的能力。");

        var missing = MissingCapabilities.ToBuilder();

        foreach (JsonProperty property in Object(capabilities, "unimplemented").EnumerateObject())
        {
            Require(property.Name.Length is > 0 and <= 128 && !Available.Contains(property.Name), "未实现能力声明无效。");

            string reason = StringValue(property.Value, 4096);

            // Data packages cannot enable capabilities the program knows are missing. Add only further limitations listed by the package.
            if (!missing.ContainsKey(property.Name)) missing.Add(property.Name, reason);
        }

        var summary = new NativeServerDataSummary(1, maps.Length, modes.Length, prefixes.Length, documents.Length,
            sectionCount, assignmentCount, unparsedCount, claimedComplete, Available, missing.ToImmutable());

        return new NativeServerData(summary, maps, modes, prefixes, documents);
    }

    private static Dictionary<string, JsonElement> ValidateSources(JsonElement manifest)
    {
        var sources = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (JsonElement source in Array(manifest, "sources", SourcePurposes.Count, SourcePurposes.Count).EnumerateArray())
        {
            string name = Text(source, "name", 64);

            Require(SourcePurposes.TryGetValue(name, out string? purpose) && !sources.ContainsKey(name) && Text(source, "purpose", 64) == purpose,
                "清单包含未知、重复或用途不匹配的来源文件。");
            Integer(source, "source_bytes", 1, 8 * 1024 * 1024);
            Integer(source, "decoded_bytes", 1, MaximumSourceLine);
            Digest(source, "sha256"); Digest(source, "decoded_sha256");
            Object(source, "statistics");
            sources.Add(name, source);
        }
        return sources;
    }

    private static ImmutableArray<NativeRuleDocument> ValidateRules(JsonElement rules, Dictionary<string, JsonElement> sources)
    {
        var documents = ImmutableArray.CreateBuilder<NativeRuleDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int totalEntries = 0, totalSections = 0;

        foreach (JsonElement document in Array(rules, "documents", SourcePurposes.Count - 1, SourcePurposes.Count - 1).EnumerateArray())
        {
            string source = Text(document, "source", 64), purpose = Text(document, "purpose", 64);

            Require(source != "DefaultGame.ini" && SourcePurposes.TryGetValue(source, out var expected) && purpose == expected && seen.Add(source),
                "规则包含未知、重复或用途不匹配的来源。");

            var sections = ImmutableArray.CreateBuilder<NativeRuleSection>();

            foreach (JsonElement section in Array(document, "sections", 1, 8192).EnumerateArray())
            {
                Require(++totalSections <= 32768, "配置节总数超出上限。");

                int line = checked((int)Integer(section, "line", 1, MaximumSourceLine));
                string name = Text(section, "name", 4096, true);
                var entries = ImmutableArray.CreateBuilder<NativeRuleEntry>();
                var unparsed = ImmutableArray.CreateBuilder<NativeUnparsedRuleLine>();

                foreach (JsonElement entry in Array(section, "entries", 0, MaximumEntries).EnumerateArray())
                {
                    Require(++totalEntries <= MaximumEntries, "配置记录总数超出上限。");

                    string key = Text(entry, "key", MaximumTextLength), value = Text(entry, "value", MaximumTextLength, true);

                    Require(key.Length + value.Length <= MaximumTextLength, "配置赋值文本超过长度上限。");
                    entries.Add(new NativeRuleEntry(key, value, checked((int)Integer(entry, "line", line, MaximumSourceLine))));
                }
                foreach (JsonElement raw in Array(section, "unparsed_lines", 0, MaximumEntries).EnumerateArray())
                {
                    Require(++totalEntries <= MaximumEntries, "配置记录总数超出上限。");
                    unparsed.Add(new NativeUnparsedRuleLine(Text(raw, "text", MaximumTextLength, true),
                        checked((int)Integer(raw, "line", line, MaximumSourceLine))));
                }
                sections.Add(new NativeRuleSection(name, line, entries.ToImmutable(), unparsed.ToImmutable()));
            }

            JsonElement statistics = Object(sources[source], "statistics");

            Require(Integer(statistics, "sections", 1, 8192) == sections.Count &&
                Integer(statistics, "assignments", 0, MaximumEntries) == sections.Sum(section => section.Entries.Length) &&
                Integer(statistics, "unparsed_lines", 0, MaximumEntries) == sections.Sum(section => section.UnparsedLines.Length),
                $"{source} 的配置统计不一致。");
            documents.Add(new NativeRuleDocument(source, purpose, sections.ToImmutable()));
        }
        return documents.ToImmutable();
    }

    private static NativeDirectoryOrigin Origin(JsonElement row)
    {
        string operation = Text(row, "operation", 1, true);

        Require(operation is "" or "+" or "-" or "!" or ".", "地图目录包含未知配置操作符。");
        return new(Text(row, "source_section", 4096, true), checked((int)Integer(row, "source_line", 1, MaximumSourceLine)),
            operation, Text(row, "raw_value", MaximumTextLength, true));
    }

    private static void EnsureDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        Require((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0,
            "原生数据路径必须是普通目录，不能通过链接重定向。");
    }

    private static byte[] ReadBounded(string directory, string name, int maximum)
    {
        Require(FileNames.Contains(name, StringComparer.Ordinal), "拒绝读取非白名单原生数据文件。");

        string path = Path.Combine(directory, name);

        Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0,
            $"{name} 必须为普通文件。");

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

        Require(input.Length is > 0 && input.Length <= maximum, $"{name} 文件长度超出上限。");

        byte[] bytes = new byte[checked((int)input.Length)];

        input.ReadExactly(bytes);
        Require(input.ReadByte() == -1, $"{name} 在读取期间改变了长度。");
        return bytes;
    }

    private static JsonDocument Parse(byte[] bytes, string name)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            var objects = new Stack<HashSet<string>>();
            int tokens = 0;

            while (reader.Read())
            {
                Require(++tokens <= 1_000_000, $"{name} JSON 节点数量超出上限。");
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    Require(reader.ValueSpan.Length <= 768, $"{name} JSON 属性名称超长。");

                    string key = reader.GetString()!;

                    Require(key.Length <= 128 && objects.Peek().Add(key), $"{name} 包含重复或超长 JSON 属性。");
                }
                else if (reader.TokenType == JsonTokenType.String)
                    Require(reader.ValueSpan.Length <= MaximumTextLength * 6, $"{name} JSON 文本超长。");
            }
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException error) { throw new InvalidDataException($"{name} JSON 结构无效。", error); }
    }

    private static void Schema(JsonElement root, string name) => Require(Integer(root, "schema_version", 1, 1) == 1, $"{name} schema 不受支持。");
    private static JsonElement Member(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _), $"缺少原生数据字段 {name}。");
        return value.GetProperty(name);
    }
    private static JsonElement Object(JsonElement value, string name)
    {
        JsonElement result = Member(value, name);

        Require(result.ValueKind == JsonValueKind.Object, $"原生数据字段 {name} 必须为对象。");
        return result;
    }
    private static JsonElement Array(JsonElement value, string name, int minimum, int maximum)
    {
        JsonElement result = Member(value, name);

        Require(result.ValueKind == JsonValueKind.Array && result.GetArrayLength() >= minimum && result.GetArrayLength() <= maximum,
            $"原生数据数组 {name} 的类型或数量无效。");
        return result;
    }
    private static long Integer(JsonElement value, string name, long minimum, long maximum)
    {
        JsonElement result = Member(value, name);

        Require(result.ValueKind == JsonValueKind.Number && result.TryGetInt64(out _), $"原生数据字段 {name} 必须为整数。");

        long number = result.GetInt64();

        Require(number >= minimum && number <= maximum, $"原生数据字段 {name} 超出范围。");
        return number;
    }
    private static bool Boolean(JsonElement value, string name)
    {
        JsonElement result = Member(value, name);

        Require(result.ValueKind is JsonValueKind.True or JsonValueKind.False, $"原生数据字段 {name} 必须为布尔值。");
        return result.GetBoolean();
    }
    private static string Text(JsonElement value, string name, int maximum, bool allowEmpty = false) => StringValue(Member(value, name), maximum, allowEmpty);
    private static string StringValue(JsonElement value, int maximum, bool allowEmpty = false)
    {
        Require(value.ValueKind == JsonValueKind.String, "原生数据文本字段类型无效。");

        string text = value.GetString()!;

        Require(text.Length <= maximum && (allowEmpty || text.Length > 0) && !text.Contains('\0'), "原生数据文本长度或内容无效。");
        return text;
    }
    private static string Digest(JsonElement value, string name)
    {
        string digest = Text(value, name, 64);

        Require(digest.Length == 64 && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'), "原生数据 SHA-256 格式无效。");
        return digest;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

/// <summary>
/// Actual validation results. Integrity and world-backend status depend on program capabilities, not a
/// manifest's completion claims.
/// </summary>
public sealed record NativeServerDataSummary(int SchemaVersion, int MapCount, int ModeCount, int PrefixCount,
    int RuleDocumentCount, int RuleSectionCount, int RuleAssignmentCount, int UnparsedLineCount,
    bool ManifestClaimsCompleteServer, ImmutableArray<string> AvailableCapabilities,
    ImmutableDictionary<string, string> UnimplementedCapabilities)
{
    public bool HasWorldBackend => false;
    public bool IsCompleteGameServer => false;
}

/// <summary>
/// Unevaluated configuration-catalog source. Preserves operators and raw text; merging must follow original
/// record order.
/// </summary>
public sealed record NativeDirectoryOrigin(string SourceSection, int SourceLine, string Operation, string RawValue);
public sealed record NativeMapDefinition(ushort MapId, string MapName, string DisplayName, NativeDirectoryOrigin Origin);
public sealed record NativeModeDefinition(uint ModeId, string GameClass, NativeDirectoryOrigin Origin);
public sealed record NativeMapPrefix(string Prefix, string GameClass, bool UsesCommonPackage, NativeDirectoryOrigin Origin);
/// <summary>
/// Immutable configuration documents and child records preserve duplicate keys, duplicate sections, and
/// unparsed lines in original order.
/// </summary>
public sealed record NativeRuleDocument(string Source, string Purpose, ImmutableArray<NativeRuleSection> Sections);
public sealed record NativeRuleSection(string Name, int Line, ImmutableArray<NativeRuleEntry> Entries, ImmutableArray<NativeUnparsedRuleLine> UnparsedLines);
public sealed record NativeRuleEntry(string Key, string Value, int Line);
public sealed record NativeUnparsedRuleLine(string Text, int Line);
