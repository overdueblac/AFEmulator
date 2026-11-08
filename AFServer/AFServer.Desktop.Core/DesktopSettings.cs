using System.Text.Json;
using System.Text.Json.Nodes;
using AFServer.AssaultFire.Hosting;

namespace AFServer.Desktop;

/// <summary>
/// The UI merges local MongoDB-management fields only, preserving wallets, accounts, certificates, and
/// AFDEV settings.
/// </summary>
internal static class DesktopSettings
{
    public static ServerOptions Read() => ServerOptions.Load(AppContext.BaseDirectory);

    /// <summary>
    /// Stores paths within the directory as portable relative paths; outside paths retain the user's
    /// original spelling.
    /// </summary>
    public static string NormalizeMongoPath(string path, string? baseDirectory = null)
    {
        path = path.Trim();
        baseDirectory ??= AppContext.BaseDirectory;

        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new ArgumentException("MongoDB 路径须为相对 EXE 的路径或完整绝对路径。");

        var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), baseDirectory);
        var relative = Path.GetRelativePath(baseDirectory, resolved);

        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return path;

        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Atomically replaces configuration while preserving fields unmanaged by the UI. Failure leaves the
    /// original intact.
    /// </summary>
    public static void SaveMongoPaths(string executable, string dataDirectory, bool autoStart, string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        executable = NormalizeMongoPath(executable, baseDirectory);
        dataDirectory = NormalizeMongoPath(dataDirectory, baseDirectory);

        var local = new MongoLocalProcessOptions { Executable = executable, DataDirectory = dataDirectory, AutoStart = autoStart };

        local.Validate();

        var path = Path.Combine(baseDirectory, "appsettings.json");
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject()
                ?? throw new InvalidDataException("配置不能为空。")
            : new JsonObject();
        var key = Key(root, "MongoLocalProcess");
        var process = root[key] as JsonObject;

        if (root[key] is not null && process is null) throw new InvalidDataException("MongoLocalProcess 必须是对象。");
        if (process is null) root[key] = process = new JsonObject();

        process[Key(process, "Executable")] = executable;
        process[Key(process, "DataDirectory")] = dataDirectory;
        process[Key(process, "AutoStart")] = autoStart;

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(root, DesktopJsonContext.Indented.JsonObject) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Key(JsonObject parent, string name) => parent.Select(pair => pair.Key)
        .FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
}
