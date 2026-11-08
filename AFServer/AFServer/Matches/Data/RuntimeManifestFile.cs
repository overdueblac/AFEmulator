using System.Text.Json.Serialization;

namespace AFServer.AssaultFire.Matches;

/// <summary>A released resource's relative path, exact byte length, and complete SHA-256.</summary>
public sealed class RuntimeManifestFile
{
    /// <summary>
    /// Path relative to the runtime root. Manifest comparisons follow Windows case-insensitive semantics.
    /// </summary>
    [JsonPropertyName("relative_path"), JsonRequired]
    public string RelativePath { get; set; } = "";

    /// <summary>Exact file length in bytes.</summary>
    [JsonPropertyName("size"), JsonRequired]
    public long Size { get; set; }

    /// <summary>Hexadecimal SHA-256 of the complete file.</summary>
    [JsonPropertyName("sha256"), JsonRequired]
    public string Sha256 { get; set; } = "";
}
