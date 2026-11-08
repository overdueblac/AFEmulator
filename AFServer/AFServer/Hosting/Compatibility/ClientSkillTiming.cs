using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AFServer.AssaultFire.Protocol;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Reads the native skill-configuration container. Client and AFDEV read durations directly; login packets
/// cannot override them. A single-byte mapping changes ASCII numbers only, preserving GBK text, comments,
/// and original newline bytes.
/// </summary>
public static class ClientSkillTiming
{
    private static readonly Regex Field = new(
        @"^(?<prefix>\s*(?<key>PlusTime|SkillPlusTimeIncrease|SkillCooldownTimeDecrease|CooldownTime)\s*=\s*)(?<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)(?<suffix>\s*(?:[;#].*)?)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public sealed record Entry(string Section, string Key, double Value);

    /// <summary>Decodes the skill INI through the shared client-configuration container.</summary>
    public static byte[] Decode(byte[] blob) => ClientConfigurationContainer.Decode(blob);

    /// <summary>Encodes the skill INI in the native client-container format.</summary>
    public static byte[] Encode(byte[] raw) => ClientConfigurationContainer.Encode(raw);

    public static IReadOnlyList<Entry> Inspect(byte[] raw)
    {
        List<Entry> entries = [];

        Transform(raw, (section, key, value) => { entries.Add(new Entry(section, key, value)); return value; });
        return entries;
    }

    /// <summary>
    /// Changes only positive PVEGame.Skill_* values. Zero and negative values retain native
    /// unlimited/disabled semantics.
    /// </summary>
    public static byte[] Adjust(byte[] raw, double duration, double cooldown, bool scale = false, double cooldownFloor = 1)
    {
        if (!double.IsFinite(duration) || !double.IsFinite(cooldown) || duration <= 0 || cooldown <= 0 ||
            !double.IsFinite(cooldownFloor) || cooldownFloor < 1 || (!scale && (duration > 3600 || cooldown > 1e9)))
            throw new ArgumentException("持续时间和冷却必须为有限正数，固定持续时间最多 3600 秒。");
        return Transform(raw, (_, key, value) => value <= 0 ? value :
            key == "CooldownTime" ? (scale ? Math.Clamp(value * cooldown, cooldownFloor, 1e9) : cooldown) :
            scale ? Math.Clamp(value * duration, 0, 3600) : duration);
    }

    private static byte[] Transform(byte[] raw, Func<string, string, double, double> transform)
    {
        var parts = Regex.Split(Encoding.Latin1.GetString(raw), "(\\r\\n|\\n|\\r)");
        var section = "";
        var fields = 0;

        for (var index = 0; index < parts.Length; index += 2)
        {
            var line = parts[index].Trim();

            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            if (!section.StartsWith("PVEGame.Skill_", StringComparison.Ordinal)) continue;

            var match = Field.Match(parts[index]);

            if (!match.Success) continue;
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var old) || !double.IsFinite(old))
                throw new InvalidDataException("技能配置数值无效。");
            fields++;

            var value = transform(section, match.Groups["key"].Value, old);

            if (value == old) continue;
            if (old > 0 && ((float)value <= 0 || !float.IsFinite((float)value)))
                throw new ArgumentException("技能数值超出客户端浮点范围，未修改文件。");

            var formatted = value.ToString("R", CultureInfo.InvariantCulture);

            if (match.Groups["value"].Value.Contains('.') && !formatted.Contains('.') && !formatted.Contains('E')) formatted += ".0";
            parts[index] = match.Groups["prefix"].Value + formatted + match.Groups["suffix"].Value;
        }
        if (fields == 0) throw new InvalidDataException("未找到受支持的 PVE 技能字段，未修改文件。");
        return Encoding.Latin1.GetBytes(string.Concat(parts));
    }

    /// <summary>
    /// Validates the temporary container before atomic replacement and retains an original backup.
    /// Reapplying identical fixed values leaves the file unchanged.
    /// </summary>
    public static string? ApplyFile(string path, double duration, double cooldown, bool scale = false, double cooldownFloor = 1)
    {
        var original = ReadBounded(path);
        var raw = Decode(original);
        var adjusted = Adjust(raw, duration, cooldown, scale, cooldownFloor);

        if (raw.AsSpan().SequenceEqual(adjusted)) return null;
        return ReplaceWithBackup(path, Encode(adjusted), original);
    }

    /// <summary>
    /// Supports legacy Python .bak_skill_* backups. Backs up the current file and validates the target
    /// configuration before restoring.
    /// </summary>
    public static string RestoreFile(string path, string backupPath)
    {
        var restored = ReadBounded(backupPath);

        _ = Inspect(Decode(restored));
        return ReplaceWithBackup(path, restored, ReadBounded(path));
    }

    public static byte[] ReadBounded(string path) => ClientConfigurationContainer.ReadBounded(path);

    private static string ReplaceWithBackup(string path, byte[] replacement, byte[] expectedOriginal)
    {
        var backup = path + ".bak_skill_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture);
        var temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllBytes(temporary, replacement);
            if (!Decode(File.ReadAllBytes(temporary)).AsSpan().SequenceEqual(Decode(replacement)))
                throw new IOException("技能配置写回校验失败。");
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expectedOriginal))
                throw new IOException("技能配置已被其他程序更新，请重新执行。");
            File.Replace(temporary, path, backup);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
