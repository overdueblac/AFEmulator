using System.Globalization;

namespace AFConnect.EDGE.Client;

/// <summary>
/// Parses top-level INI structure fields while preserving quotes, nested arrays, unrelated strings, and
/// formatting.
/// </summary>
internal sealed class ClientIniRecord
{
    private readonly string line;
    private readonly Dictionary<string, (int Start, int Length)> fields = new(StringComparer.Ordinal);

    internal string Original => line;
    internal bool Contains(string key) => fields.ContainsKey(key);

    internal ClientIniRecord(string line)
    {
        this.line = line;

        var open = line.IndexOf('(');
        var close = line.LastIndexOf(')');
        var start = open + 1;
        var depth = 0;
        var quoted = false;

        if (open < 0 || close <= open) throw new InvalidDataException("客户端 INI 结构行缺少括号。");

        for (var index = start; index <= close; index++)
        {
            var character = line[index];

            // Latin1 preserves the original GBK bytes; a multibyte character's trailing byte may be a backslash.
            if (quoted && character is >= '\u0081' and <= '\u00FE') { index++; continue; }
            if (quoted && character == '\\') { index++; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (character == '(') depth++;
            if (character == ')' && index != close) depth--;
            if (index != close && (character != ',' || depth != 0)) continue;
            if (depth != 0) throw new InvalidDataException("客户端 INI 子数组括号不匹配。");

            var equals = line.IndexOf('=', start, index - start);

            if (equals < start) throw new InvalidDataException("客户端 INI 结构字段缺少等号。");

            var key = line[start..equals].Trim();
            var valueStart = equals + 1;
            var valueEnd = index;

            while (valueStart < valueEnd && char.IsWhiteSpace(line[valueStart])) valueStart++;
            while (valueEnd > valueStart && char.IsWhiteSpace(line[valueEnd - 1])) valueEnd--;
            if (!fields.TryAdd(key, (valueStart, valueEnd - valueStart)))
                throw new InvalidDataException($"客户端 INI 含有重复字段：{key}。");

            start = index + 1;
        }
        if (quoted || start != close + 1) throw new InvalidDataException("客户端 INI 字符串或结构行未闭合。");
    }

    internal string Value(string key)
    {
        var field = Field(key);

        return line.Substring(field.Start, field.Length);
    }

    internal int Number(string key) => int.Parse(Value(key), CultureInfo.InvariantCulture);

    internal int[] Numbers(string key)
    {
        var value = Value(key);

        if (!value.StartsWith('(') || !value.EndsWith(')'))
            throw new InvalidDataException($"客户端 INI 字段 {key} 不是数组。");

        return value[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
    }

    internal string Replace(IReadOnlyDictionary<string, string> replacements)
    {
        var result = line;

        foreach (var replacement in replacements.Select(pair => (Field: Field(pair.Key), pair.Value))
            .OrderByDescending(pair => pair.Field.Start))
            result = result.Remove(replacement.Field.Start, replacement.Field.Length)
                .Insert(replacement.Field.Start, replacement.Value);

        return result;
    }

    private (int Start, int Length) Field(string key) => fields.TryGetValue(key, out var field) ? field :
        throw new InvalidDataException($"客户端 INI 缺少字段：{key}。");
}
