using System.Text.Json;
using System.Text.Json.Serialization;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Release manifest for a controlled AFDEV package. Describes versions and resources only, with no business
/// state or credentials.
/// </summary>
public sealed partial class RuntimeManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>This runtime package's release version.</summary>
    [JsonPropertyName("runtime_version"), JsonRequired]
    public string RuntimeVersion { get; set; } = "";

    /// <summary>
    /// Complete SHA-256 of the original TGame used to build AFDEV. The original executable is not included
    /// in this runtime package.
    /// </summary>
    [JsonPropertyName("game_build_sha256"), JsonRequired]
    public string GameBuildSha256 { get; set; } = "";

    /// <summary>Complete file SHA-256 of the prebuilt, patched AFDEV executable.</summary>
    [JsonPropertyName("afdev_sha256"), JsonRequired]
    public string AfdevSha256 { get; set; } = "";

    /// <summary>
    /// SHA-256 of the ServerMove v4 patch contents. Preflight validates the actual patch layout.
    /// </summary>
    [JsonPropertyName("servermove_v4_sha256"), JsonRequired]
    public string ServerMoveV4Sha256 { get; set; } = "";

    /// <summary>Version of the exact mode/map routing allowlist.</summary>
    [JsonPropertyName("mode_map_catalog_version"), JsonRequired]
    public string ModeMapCatalogVersion { get; set; } = "";

    /// <summary>Complete file set for the runtime package. Unlisted extra files are rejected.</summary>
    [JsonPropertyName("files"), JsonRequired]
    public List<RuntimeManifestFile> Files { get; set; } = [];

    /// <summary>
    /// Locates the unique main map among registered resources using actual manifest subdirectories, without
    /// inferring paths from the first map.
    /// </summary>
    internal RuntimeManifestFile ResolveMainMap(string mapName)
    {
        RuntimeManifestFile[] maps = Files.Where(file =>
            RuntimePath.NormalizeRelativePath(file.RelativePath).StartsWith("TGame/CookedPC/Maps/", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(file.RelativePath), mapName + ".udk", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (maps.Length != 1)
            throw new InvalidDataException($"AFDEV 目标地图必须在 manifest 中唯一出现：{mapName}.udk");

        return maps[0];
    }

    /// <summary>
    /// Reads and validates manifest structure. Runtime preflight verifies file contents, PE platform, and
    /// patches.
    /// </summary>
    public static RuntimeManifest Load(string path)
    {
        var fullPath = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, path);

        RuntimePath.RejectReparsePoints(fullPath);

        try
        {
            using var stream = File.OpenRead(fullPath);
            using var document = JsonDocument.Parse(stream);

            RejectDuplicateProperties(document.RootElement);

            var manifest = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Deserialize<RuntimeManifest>(document.RootElement, JsonOptions) ??
                throw new InvalidDataException("AFDEV manifest 不能为空。");

            manifest.ValidateMetadata();

            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"AFDEV manifest JSON 无效：{fullPath}", exception);
        }
    }

    /// <summary>
    /// Writes a temporary file beside the target and publishes atomically. Never overwrites an existing
    /// target; callers must not mutate the DTO during the call.
    /// </summary>
    public void Save(string path)
    {
        ValidateMetadata();

        var fullPath = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        RuntimePath.RejectReparsePoints(fullPath);
        Directory.CreateDirectory(directory);
        RuntimePath.RejectReparsePoints(directory);

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(stream, this, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Validates required versions, hashes, resource paths, and case-insensitive uniqueness without reading
    /// resources.
    /// </summary>
    public void ValidateMetadata()
    {
        ValidateVersion(RuntimeVersion, "runtime_version");
        ValidateVersion(ModeMapCatalogVersion, "mode_map_catalog_version");
        ValidateSha256(GameBuildSha256, "game_build_sha256");
        ValidateSha256(AfdevSha256, "afdev_sha256");
        ValidateSha256(ServerMoveV4Sha256, "servermove_v4_sha256");

        if (Files is null || Files.Count == 0)
            throw new InvalidDataException("AFDEV manifest 必须包含完整资源清单。");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RuntimeManifestFile? afdev = null;

        foreach (var file in Files)
        {
            if (file is null || file.Size < 0)
                throw new InvalidDataException("AFDEV manifest 文件项或字节长度无效。");

            var normalized = RuntimePath.ValidateRuntimeFilePath(file.RelativePath);

            if (!paths.Add(normalized))
                throw new InvalidDataException($"AFDEV manifest 包含大小写重复路径：{file.RelativePath}");

            ValidateSha256(file.Sha256, file.RelativePath);

            if (string.Equals(normalized, RuntimePath.AfdevRelativePath, StringComparison.OrdinalIgnoreCase))
                afdev = file;

            if (RuntimePath.IsAfdevExecutablePath(normalized) &&
                !string.Equals(file.Sha256, AfdevSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AFDEV manifest 的两个宿主 EXE 必须与 afdev_sha256 使用同一构建。");
        }

        if (afdev is null || !string.Equals(afdev.Sha256, AfdevSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AFDEV manifest 必须包含与 afdev_sha256 一致的预制 AFDEV 文件项。");
    }

    private static void ValidateVersion(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new InvalidDataException($"AFDEV manifest 版本字段无效：{field}");
    }

    private static void ValidateSha256(string value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"AFDEV manifest SHA-256 无效：{field}");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"AFDEV manifest JSON 包含重复字段：{property.Name}");

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}
