using System.Text;
using AFServer.AssaultFire.Hosting;

namespace AFServer.Desktop;

/// <summary>
/// Forwards Console summaries to the GUI. Full desktop output is saved separately, unaffected by window
/// clearing or queue limits.
/// </summary>
internal sealed class MonitorTextWriter(RuntimeMonitor monitor) : TextWriter
{
    private readonly StringBuilder pending = new();
    private readonly object gate = new();
    private readonly StreamWriter transcript = CreateTranscript();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (gate)
        {
            if (value == '\n')
            {
                Emit(pending.ToString().TrimEnd('\r'));
                pending.Clear();
            }
            else
            {
                pending.Append(value);
                if (pending.Length >= 16000)
                {
                    Emit(pending.ToString());
                    pending.Clear();
                }
            }
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (gate)
        {
            foreach (var character in value)
            {
                Write(character);
            }
        }
    }

    public override void Flush()
    {
        lock (gate)
        {
            if (pending.Length > 0)
            {
                Emit(pending.ToString());
                pending.Clear();
            }
        }
    }

    private void Emit(string line)
    {
        transcript.WriteLine(line);
        monitor.Write(false, line);
    }

    private static StreamWriter CreateTranscript()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "logs");

        Directory.CreateDirectory(directory);
        return new StreamWriter(new FileStream(Path.Combine(directory, $"desktop-{DateTime.UtcNow:yyyyMMdd}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                Flush();
                transcript.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
