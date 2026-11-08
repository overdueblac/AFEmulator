using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AFServer.Desktop;

internal sealed record DesktopHistoryEntry(DateTimeOffset at, string kind, string message);

/// <summary>
/// Static JSON metadata for desktop history, configuration, and diagnostics supports Native AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(DesktopHistoryEntry))]
[JsonSerializable(typeof(JsonObject))]
internal partial class DesktopJsonContext : JsonSerializerContext
{
    internal static DesktopJsonContext Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}
