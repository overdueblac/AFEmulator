using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Replaces local maintenance scripts from the old directory. Commands neither open listeners nor connect
/// to or modify the player database.
/// </summary>
public static partial class MaintenanceCommands
{
    public const string Help = "维护：--skill-timing show|uniform|boost|extreme；--skill-timing fixed 持续秒 冷却秒；" +
        "--skill-timing scale 持续倍率 冷却倍率；--skill-timing restore 备份路径；--bots status|team|mutant|off；--bot-logs。";

    public static bool TryRun(string[] args, ServerOptions options, string baseDirectory, TextWriter output)
    {
        if (args.Length == 0) return false;
        switch (args[0])
        {
            case "--skill-timing":
                RunSkillTiming(args, options, output);
                return true;
            case "--bots":
                if (args.Length != 2) throw new ArgumentException("用法：--bots status|team|mutant|off");
                ConfigureBots(args[1], baseDirectory, output);
                return true;
            case "--bot-logs":
                if (args.Length != 1) throw new ArgumentException("--bot-logs 不接受附加参数。");
                if (options.MatchBackend == "Afdev")
                {
                    output.WriteLine("后端：Afdev。普通 BIO 单真人、单 Bot 实验诊断；记录不等同于完整可玩验收。");
                    output.WriteLine($"现有原生实例诊断：{Path.GetFullPath(options.MatchRuntime.InstancesRoot, baseDirectory)}");
                    output.WriteLine("各尝试的 logs/lifecycle.jsonl 记录宿主阶段；logs/bio-bot/ 记录原生命令、Bot 准备与激活证据。");
                }
                else if (options.MatchBackend == "Dedicated")
                {
                    output.WriteLine("后端：Dedicated。以下为兼容代理的 Bot 日志。");
                    PrintBotLogs(options.ProxyRoot, output);
                }
                else throw new InvalidDataException("MatchBackend 必须为 Dedicated 或 Afdev。");
                return true;
            default:
                return false;
        }
    }

    private static void RunSkillTiming(string[] args, ServerOptions options, TextWriter output)
    {
        if (args.Length < 2) throw new ArgumentException(Help);

        var path = Path.Combine(options.ClientRoot, "TGame", "Config", "DefaultSkill.ini");
        var action = args[1];
        var expectedLength = action == "restore" ? 3 : action is "fixed" or "scale" ? 4 : 2;

        if (args.Length != expectedLength) throw new ArgumentException(Help);
        if (action == "show")
        {
            foreach (var entry in ClientSkillTiming.Inspect(ClientSkillTiming.Decode(ClientSkillTiming.ReadBounded(path))))
                output.WriteLine($"{entry.Section} {entry.Key}={entry.Value.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        string? backup;

        if (action == "restore") backup = ClientSkillTiming.RestoreFile(path, Path.GetFullPath(args[2]));
        else
        {
            var (duration, cooldown, scale, floor) = action switch
            {
                "uniform" => (40d, 1d, false, 1d),
                "boost" => (5d, 0.2d, true, 2d),
                // Matches the old tool's extreme multipliers, 3,600-second ceiling, and one-second floor.
                "extreme" => (1000d, 0.001d, true, 1d),
                "fixed" => (Number(args[2]), Number(args[3]), false, 1d),
                "scale" => (Number(args[2]), Number(args[3]), true, 1d),
                _ => throw new ArgumentException(Help)
            };
            backup = ClientSkillTiming.ApplyFile(path, duration, cooldown, scale, floor);
        }
        output.WriteLine(backup is null ? "技能数值已符合目标，未改写文件。" : $"技能配置已写入并校验；原文件备份：{backup}");
        output.WriteLine("客户端和原生对局会在下次启动时读取这些数值。");
    }

    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? value : throw new ArgumentException("请输入有限数值，小数点使用英文句点。");

    /// <summary>
    /// Reports or disables bots for the deployed backend. Dedicated retains original presets; Afdev manages
    /// only the ordinary BIO experiment switch.
    /// </summary>
    /// <remarks>
    /// Edits preserve unknown settings and back up the original file, taking effect after restart. Queries
    /// do not write files or establish current runtime state.
    /// </remarks>
    public static void ConfigureBots(string preset, string baseDirectory, TextWriter output)
    {
        if (preset is not ("status" or "team" or "mutant" or "off")) throw new ArgumentException("未知人机预设。使用 status、team、mutant 或 off。");

        var path = Path.Combine(baseDirectory, "appsettings.json");
        var original = File.ReadAllBytes(path);
        var json = original.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? original.AsMemory(Encoding.UTF8.Preamble.Length) : original.AsMemory();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("appsettings.json 必须是配置对象；配置未修改。");

        // Startup deserialization uses the last matching key. Maintenance writes must first reject duplicate and case-variant keys to avoid selecting the wrong backend.
        var backends = document.RootElement.EnumerateObject()
            .Where(property => property.Name.Equals("MatchBackend", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (backends.Length > 1)
            throw new InvalidDataException("appsettings.json 包含重复或歧义的 MatchBackend 配置项；配置未修改。");

        var backend = backends.Length == 0 ? "Dedicated" :
            backends[0].Value.ValueKind == JsonValueKind.String ? backends[0].Value.GetString() : null;

        if (backend == "Afdev")
        {
            if (preset is "team" or "mutant")
                throw new InvalidOperationException("Afdev 仅提供 MatchRuntime.BioBots.Enabled 的单真人、单 Bot 实验；不支持兼容代理的 team/mutant 预设，配置未修改。");

            ConfigureAfdevBots(preset, path, original, json, document.RootElement, output);

            return;
        }

        if (backend != "Dedicated") throw new InvalidDataException("MatchBackend 必须为 Dedicated 或 Afdev；配置未修改。");

        output.WriteLine("后端：Dedicated。以下 Matches.Bot 参数仅用于兼容代理，修改后重启服务端生效。");

        var root = JsonNode.Parse(json.Span, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        var matchesKey = root.Select(pair => pair.Key).FirstOrDefault(key => key.Equals("Matches", StringComparison.OrdinalIgnoreCase)) ?? "Matches";
        var matches = root[matchesKey]?.AsObject() ?? new JsonObject();

        if (preset == "status")
        {
            var defaults = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToNode(new Matches.MatchServerOptions())!.AsObject();

            foreach (var pair in matches)
            {
                var key = defaults.Select(value => value.Key).FirstOrDefault(value => value.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) ?? pair.Key;

                defaults[key] = pair.Value?.DeepClone();
            }
            foreach (var pair in defaults.Where(pair => pair.Key.StartsWith("Bot", StringComparison.Ordinal))) output.WriteLine($"{pair.Key}: {pair.Value?.ToJsonString()}");
            return;
        }
        void Set(string key, JsonNode? value)
        {
            foreach (var existing in matches.Select(pair => pair.Key).Where(existing => existing.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray()) matches.Remove(existing);
            matches[key] = value;
        }
        Set("BotFill", JsonValue.Create(preset != "off"));
        if (preset != "off")
        {
            Set("BotModes", preset == "team" ? new JsonArray(0x204, 0x209, 0x1002) : new JsonArray(0x204, 0x209));
            Set("BotCountOverride", JsonValue.Create(-1));
            Set("BotRevive", JsonValue.Create(false));
            Set("BotAiAbilityCall", JsonValue.Create(false));
            Set("BotMinReadyPlayers", JsonValue.Create(1));
            Set("BotTopupIntervalSeconds", JsonValue.Create(5));
            Set("BotSkillIndex", JsonValue.Create(6));
            Set("BotDifficulty", JsonValue.Create(1d));
        }
        if (root[matchesKey] is null) root[matchesKey] = matches;

        string backup = WriteBotConfiguration(path, original, root);

        output.WriteLine($"人机配置已写入 {path}，预设 {preset}，重启服务端后生效。备份：{backup}");
    }

    private static void ConfigureAfdevBots(string preset, string path, byte[] original, ReadOnlyMemory<byte> json,
        JsonElement document, TextWriter output)
    {
        JsonProperty? runtime = FindUniqueProperty(document, "MatchRuntime");
        JsonProperty? bots = runtime is { } runtimeProperty ? FindUniqueProperty(runtimeProperty.Value, "BioBots") : null;
        JsonProperty? flag = bots is { } botsProperty ? FindUniqueProperty(botsProperty.Value, "Enabled") : null;

        if (flag is { } value && value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("MatchRuntime.BioBots.Enabled 必须为布尔值；配置未修改。");

        bool enabled = flag?.Value.GetBoolean() ?? false;

        output.WriteLine("后端：Afdev。普通变异战支持变异地带（74）、欢乐海岸（122）、恐怖之城（39）、" +
            "老城区（118）和考古基地（60），按本局参战容量补满 Bot，默认关闭。");
        output.WriteLine($"磁盘配置 MatchRuntime.BioBots.Enabled: {enabled.ToString().ToLowerInvariant()}；修改后重启服务端生效。");
        output.WriteLine("启用后先准备 Bot，在原生 PendingMatch 等待精确匹配的冻结真人连接，再登记人数；等待状态不代表 Bot 已就绪。");
        output.WriteLine("出生、命中与感染由原生引擎执行；本局实际加载的地图及 Bot 准备结果记录在对局日志中。");

        if (preset == "status") return;
        if (!enabled)
        {
            output.WriteLine("实验开关已关闭，未改写配置。");
            return;
        }

        var root = JsonNode.Parse(json.Span, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();

        root[runtime!.Value.Name]![bots!.Value.Name]![flag!.Value.Name] = false;

        string backup = WriteBotConfiguration(path, original, root);

        output.WriteLine($"已设置 MatchRuntime.BioBots.Enabled=false，重启服务端后生效。备份：{backup}");
    }

    private static JsonProperty? FindUniqueProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{name} 的父配置必须为对象；配置未修改。");

        JsonProperty[] matches = parent.EnumerateObject()
            .Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (matches.Length > 1)
            throw new InvalidDataException($"appsettings.json 包含重复或歧义的 {name} 配置项；配置未修改。");

        return matches.Length == 0 ? null : matches[0];
    }

    private static string WriteBotConfiguration(string path, byte[] original, JsonObject root)
    {
        var updated = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");
        var backup = path + ".bak_bots_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture);

        try
        {
            File.WriteAllBytes(temporary, updated);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original)) throw new IOException("配置已被其他程序更新，请重新执行。");
            File.Replace(temporary, path, backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        return backup;
    }

    public static void PrintBotLogs(string proxyRoot, TextWriter output)
    {
        var root = Path.Combine(proxyRoot, "runtime", "ds");
        var logs = Directory.Exists(root) ? Directory.EnumerateFiles(root, "loader.log", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).Take(3).ToArray() : [];

        if (logs.Length == 0) { output.WriteLine($"尚无对局日志：{root}"); return; }
        foreach (var log in logs)
        {
            output.WriteLine(log.FullName);

            Queue<string> tail = new();
            using var input = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(input, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("[AFDEV-BOT-", StringComparison.Ordinal)) continue;
                tail.Enqueue(line);
                if (tail.Count > 18) tail.Dequeue();
            }
            foreach (var line in tail) output.WriteLine(line);
        }
        output.WriteLine("pawn=0x00000000 表示尚无角色身体；alive 为实际存活人机数量。");
    }
}
