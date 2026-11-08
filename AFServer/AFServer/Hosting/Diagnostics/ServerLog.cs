using System.Text;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Serializes console and file output to prevent interleaved connection logs. Never records plaintext
/// authentication messages or keys.
/// </summary>
public sealed class ServerLog : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    public string FilePath { get; }

    public ServerLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"server-{DateTime.UtcNow:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(string area, string message)
    {
        lock (_sync)
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{area}] {message}";

            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}
