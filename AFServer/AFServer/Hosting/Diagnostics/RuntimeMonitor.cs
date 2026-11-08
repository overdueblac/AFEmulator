using System.Collections.Concurrent;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Runtime state and a bounded log queue. The UI reads batches so high-frequency logging cannot block
/// protocol threads.
/// </summary>
public sealed class RuntimeMonitor
{
    private readonly ConcurrentQueue<(bool Database, string Text)> lines = new();
    private string serverState = "待机";
    private string databaseState = "待机";
    private string dedicatedState = "待机";
    private Func<string>? dedicatedStatus;

    /// <summary>
    /// Service startup, running, and shutdown state. One listening port does not establish whole-service
    /// readiness.
    /// </summary>
    public string ServerState
    {
        get => Volatile.Read(ref serverState); internal set => Volatile.Write(ref serverState, value);
    }

    /// <summary>
    /// Database connection state and process ownership, displaying external and locally created instances
    /// separately.
    /// </summary>
    public string DatabaseState
    {
        get => Volatile.Read(ref databaseState); internal set => Volatile.Write(ref databaseState, value);
    }

    /// <summary>The native DS controller's actual state, never inferred from log keywords.</summary>
    public string DedicatedState
    {
        get => Volatile.Read(ref dedicatedStatus)?.Invoke() ?? Volatile.Read(ref dedicatedState); internal set => Volatile.Write(ref dedicatedState, value);
    }

    internal Func<string>? DedicatedStatus
    {
        get => Volatile.Read(ref dedicatedStatus); set => Volatile.Write(ref dedicatedStatus, value);
    }

    /// <summary>The directory containing evidence for this server run.</summary>
    public string? RunDirectory
    {
        get; set;
    }

    /// <summary>
    /// Appends separately marked database and business logs, retaining only the latest 6,000 entries in
    /// memory.
    /// </summary>
    public void Write(bool database, string text)
    {
        lines.Enqueue((database, text.Length > 16000 ? text[..16000] + " …" : text));

        while (lines.Count > 6000)
        {
            lines.TryDequeue(out _);
        }
    }

    /// <summary>Dequeues one display log, returning false immediately when none is available.</summary>
    public bool TryRead(out (bool Database, string Text) line) => lines.TryDequeue(out line);
}
