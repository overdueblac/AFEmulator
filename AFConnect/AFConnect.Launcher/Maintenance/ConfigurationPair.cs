using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AFConnect.Launcher.Maintenance;

/// <summary>
/// Snapshots both original configurations, detects external changes before commit, and restores the first
/// file if writing the second fails.
/// </summary>
internal sealed class ConfigurationPair
{
    private readonly string serverPath;
    private readonly string connectPath;
    private readonly byte[] serverOriginal;
    private readonly byte[] connectOriginal;

    internal ConfigurationPair(string serverDirectory, string connectDirectory)
    {
        serverPath = Path.Combine(serverDirectory, "appsettings.json");
        connectPath = Path.Combine(connectDirectory, "config.json");
        serverOriginal = File.ReadAllBytes(serverPath);
        connectOriginal = File.ReadAllBytes(connectPath);
        Server = Parse(serverOriginal);
        Connect = Parse(connectOriginal);
    }

    internal JsonObject Server { get; }
    internal JsonObject Connect { get; }

    internal (string Server, string Connect) Commit(string stamp)
    {
        string serverBackup = serverPath + "." + stamp + ".bak";
        string connectBackup = connectPath + "." + stamp + ".bak";
        string serverPending = serverPath + "." + stamp + ".tmp";
        string connectPending = connectPath + "." + stamp + ".tmp";

        try
        {
            WriteNew(serverPending, Serialize(Server));
            WriteNew(connectPending, Serialize(Connect));

            if (!File.ReadAllBytes(serverPath).AsSpan().SequenceEqual(serverOriginal)
                || !File.ReadAllBytes(connectPath).AsSpan().SequenceEqual(connectOriginal))
                throw new IOException("生成期间配置已被其他程序修改，未替换配置，请重新操作。");

            WriteNew(serverBackup, serverOriginal);
            WriteNew(connectBackup, connectOriginal);
            File.Move(serverPending, serverPath, overwrite: true);

            try
            {
                File.Move(connectPending, connectPath, overwrite: true);
            }
            catch (Exception writeError)
            {
                try
                {
                    File.Copy(serverBackup, serverPending);
                    File.Move(serverPending, serverPath, overwrite: true);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        $"配对写入和恢复均失败。请停止两端并还原同批次备份：{serverBackup}；{connectBackup}",
                        writeError, rollbackError);
                }

                throw new IOException($"连接端配置写入失败，服务端配置已恢复。备份：{serverBackup}；{connectBackup}", writeError);
            }

            return (serverBackup, connectBackup);
        }
        finally
        {
            DeletePending(serverPending);
            DeletePending(connectPending);
        }
    }

    private static JsonObject Parse(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return JsonNode.Parse(reader.ReadToEnd(), new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new InvalidDataException("两端配置必须是 JSON 对象。");
    }

    private static byte[] Serialize(JsonObject value) => Encoding.UTF8.GetBytes(
        value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void DeletePending(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
