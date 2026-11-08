using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Hosting;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Manages an independent proxy and native-match process per room. Startup waits only for bridge binding;
/// the first valid UDP packet triggers the native engine lazily. Holds no room membership or player data.
/// After round release, the business layer decides whether to retain the room and start again.
/// </summary>
public sealed class DedicatedServerAllocator : IMatchServerAllocator, IMatchServerHealthMonitor, IAsyncDisposable
{
    private readonly ServerOptions _server;
    private readonly ServerLog _log;
    private readonly MatchServerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ulong, Allocation> _allocations = [];
    private bool _disposeRequested;
    private bool _disposed;

    public DedicatedServerAllocator(ServerOptions server, ServerLog log, MatchServerOptions? options = null)
    {
        _server = server;
        _log = log;
        _options = options ?? new MatchServerOptions();
        _options.Validate();
    }

    public async ValueTask<MatchEndpoint> ReserveAsync(GameRoom room, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            // Instances with failed cleanup retain their original slots; other free slots may still serve independent rooms.
            foreach (var pair in _allocations.Where(pair => IsUnavailable(pair.Value)).ToArray())
            {
                try
                {
                    await StopAsync(pair.Value);
                    _allocations.Remove(pair.Key);
                }
                catch (Exception)
                {
                    // StopAsync retained pending-cleanup state and recorded the reason; this slot cannot be reused now.
                }
            }

            if (_allocations.TryGetValue(room.RoomId, out var existing))
            {
                if (IsUnavailable(existing)) throw new InvalidOperationException($"房间 {room.RoomId} 的上次分配尚未完成回收。");
                return existing.Endpoint;
            }

            var target = RoomTargetCatalog.Resolve(room.ModeId, room.MapId);
            var slot = FindFreeSlot();
            var bridge = Path.Combine(_server.ProxyRoot, "bridge", "af_ds_udp_bridge_v9_multi_peer_latch.py");
            var loader = Path.Combine(_server.ProxyRoot, "server_spawner", "AFDevLoader_v48_spawner_multi_instance.py");
            var gameDirectory = Path.Combine(_server.ClientRoot, "Binaries", "Win32");

            foreach (var path in new[] { bridge, loader, Path.Combine(gameDirectory, "TGame_AFDEV.exe") })
                if (!File.Exists(path)) throw new FileNotFoundException("对局启动文件不存在。", path);

            var instanceId = $"room-{room.RoomId}-{Guid.NewGuid():N}";
            var directory = Path.Combine(_server.ProxyRoot, "runtime", "ds", instanceId);

            Directory.CreateDirectory(directory);

            var endpoint = new MatchEndpoint(_server.PublicAddress, checked((ushort)(_options.PublicPortBase + slot)), new byte[16]);
            var start = new ProcessStartInfo(ResolvePython())
            {
                WorkingDirectory = _server.ProxyRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PYTHONIOENCODING"] = "utf-8:backslashreplace";
            start.ArgumentList.Add(bridge);
            Add(start, "listen-ip", _server.ListenAddress);
            Add(start, "listen-port", endpoint.Port);
            Add(start, "target-host", "127.0.0.1");
            Add(start, "target-port", _options.TargetPortBase + slot);
            start.ArgumentList.Add("--lazy-spawn");
            Add(start, "python-exe", start.FileName);
            Add(start, "loader-script", loader);
            Add(start, "game-dir", gameDirectory);
            Add(start, "map", target.MapName);
            Add(start, "game", target.GameClass);
            Add(start, "max-players", Math.Max(1, (int)room.FighterCapacity));
            Add(start, "mode-id", room.ModeId);
            Add(start, "map-id", room.MapId);
            Add(start, "sub-mode-id", room.SubModeId);
            Add(start, "room-flags", room.RoomFlags);
            AddBotArguments(start, room);
            Add(start, "instance-id", instanceId);
            Add(start, "pid-file", Path.Combine(directory, "afdev.pid"));
            Add(start, "ready-file", Path.Combine(directory, "ready.json"));
            Add(start, "loader-pid-file", Path.Combine(directory, "loader.pid"));
            Add(start, "loader-log", Path.Combine(directory, "loader.log"));
            Add(start, "actor-dump", Path.Combine(directory, "actors.log"));

            var statePath = Path.Combine(directory, "state.json");

            Add(start, "state-file", statePath);
            Add(start, "startup-timeout", _options.StartupTimeoutSeconds);

            var output = new StreamWriter(new FileStream(Path.Combine(directory, "bridge.log"), FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            Process process;

            try { process = Process.Start(start) ?? throw new IOException("无法创建 UDP 桥接进程。"); }
            catch { output.Dispose(); throw; }

            var allocation = new Allocation(slot, endpoint, process, directory, statePath, output);

            allocation.OutputTask = DrainAsync(process.StandardOutput, allocation);
            allocation.ErrorTask = DrainAsync(process.StandardError, allocation);
            _allocations.Add(room.RoomId, allocation);
            try
            {
                await WaitForBridgeAsync(allocation, cancellationToken);
                _log.Write("MATCH", $"房间 {room.RoomId} 桥接就绪 {endpoint.Address}:{endpoint.Port}，地图 {target.MapName}；等待首个对局 UDP 包。");
                return endpoint;
            }
            catch (Exception startupError)
            {
                try
                {
                    await StopAsync(allocation);
                    _allocations.Remove(room.RoomId);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("对局启动失败，实例仍待清理。", startupError, cleanupError);
                }

                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask<bool> HasFailedAsync(ulong roomId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            return !_allocations.TryGetValue(roomId, out var allocation) || IsUnavailable(allocation);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Releases a slot only after complete cleanup. Retains process handles and pending state on failure
    /// for later retries.
    /// </summary>
    /// <remarks>
    /// Cancellation affects only lifecycle-lock acquisition. Once cleanup starts, it must complete or
    /// explicitly fail without losing the allocation midway.
    /// </remarks>
    public async ValueTask ReleaseAsync(ulong roomId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_allocations.TryGetValue(roomId, out var allocation)) return;
            await StopAsync(allocation);
            _allocations.Remove(roomId);
            _log.Write("MATCH", $"房间 {roomId} 本回合进程已释放。");
        }
        finally { _gate.Release(); }
    }

    private string ResolvePython()
    {
        var configured = Environment.GetEnvironmentVariable("AF_DS_PYTHON");

        if (string.IsNullOrWhiteSpace(configured)) configured = _options.PythonExecutable;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathRooted(configured) ? configured : Path.GetFullPath(configured, _server.ClientRoot);

        var existing = Path.Combine(_server.ClientRoot, ".af-emulator-runtime", "venv-py312", "Scripts", "python.exe");

        if (File.Exists(existing)) return existing;
        throw new FileNotFoundException("未找到 Python；请配置 Matches.PythonExecutable 或 AF_DS_PYTHON。", existing);
    }

    private int FindFreeSlot()
    {
        for (var slot = 0; slot < _options.MaxInstances; slot++)
        {
            if (_allocations.Values.Any(a => a.Slot == slot)) continue;
            if (CanBindUdp(_server.ListenAddress, _options.PublicPortBase + slot) &&
                CanBindUdp("127.0.0.1", _options.TargetPortBase + slot)) return slot;
        }
        throw new InvalidOperationException("对局实例已满或所需 UDP 端口已被占用。");
    }

    private static bool CanBindUdp(string address, int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Parse(address), port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    private void AddBotArguments(ProcessStartInfo start, GameRoom room)
    {
        var humans = room.Members.Count(member => !member.Observer);
        var available = Math.Max(0, room.FighterCapacity - humans);
        var bots = _options.BotFill && _options.BotModes.Contains(room.ModeId)
            ? (_options.BotCountOverride >= 0 ? Math.Min(available, _options.BotCountOverride) : available) : 0;

        Add(start, "bots", bots);
        Add(start, "bot-modes", string.Join(",", _options.BotModes));
        Add(start, "bot-skill-index", _options.BotSkillIndex);
        Add(start, "bot-difficulty", _options.BotDifficulty);
        Add(start, "bot-team", _options.BotTeam);
        Add(start, "bot-retries", _options.BotRetries);
        Add(start, "bot-topup-interval", _options.BotTopupIntervalSeconds);
        Add(start, "bot-min-ready-players", _options.BotMinReadyPlayers);
        Add(start, "bot-revive", _options.BotRevive ? 1 : 0);
        Add(start, "bot-ai-ability-call", _options.BotAiAbilityCall ? 1 : 0);
    }

    private static void Add(ProcessStartInfo start, string key, object value)
    {
        start.ArgumentList.Add("--" + key);
        start.ArgumentList.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    }

    private async Task WaitForBridgeAsync(Allocation allocation, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();

        while (timer.Elapsed.TotalSeconds < _options.BridgeReadyTimeoutSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (allocation.Process.HasExited)
                throw new IOException($"UDP 桥接退出，代码 {allocation.Process.ExitCode}；详情：{allocation.Directory}");

            var state = ReadState(allocation.StatePath);

            if (state == "LISTENING") return;
            if (state == "FAILED") throw new IOException($"UDP 桥接启动失败；详情：{allocation.Directory}");
            await Task.Delay(25, cancellationToken);
        }
        throw new TimeoutException($"UDP 桥接未在期限内就绪；详情：{allocation.Directory}");
    }

    private static string? ReadState(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            return document.RootElement.TryGetProperty("state", out var state) ? state.GetString() : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static bool HasFailed(string path) => ReadState(path) is "FAILED" or "STOPPED";

    private static bool IsUnavailable(Allocation allocation) => allocation.ReleasePending ||
        allocation.Process.HasExited || HasFailed(allocation.StatePath);

    private static async Task DrainAsync(StreamReader reader, Allocation allocation)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (allocation.Output)
                {
                    if (allocation.OutputClosed) return;
                    allocation.Output.WriteLine(line);
                }
            }
        }
        catch (ObjectDisposedException) when (allocation.OutputClosed) { }
    }

    private async Task StopAsync(Allocation allocation)
    {
        allocation.ReleasePending = true;

        try
        {
            // A PID file is only a stale-state clue, not proof of process ownership. Only the exact bridge handle may initiate tree cleanup;
            // the bridge protocol cannot prove ownership of detached processes, so retain the allocation and report them explicitly.
            if (!allocation.ProcessesStopped)
            {
                CaptureRecordedProcesses(allocation);
                if (!allocation.Process.HasExited) allocation.Process.Kill(entireProcessTree: true);
                await allocation.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                CaptureRecordedProcesses(allocation);

                foreach (var pair in allocation.RecordedProcesses)
                {
                    var recorded = pair.Value;

                    if (!recorded.Process.HasExited)
                        throw new IOException($"PID 记录仍指向存活进程，无法确认归属或完成回收：{pair.Key}，" +
                            $"PID={recorded.Id}，路径={recorded.ExecutablePath}，启动时间={recorded.StartedUtc:O}。实例继续占用原槽。");
                }

                allocation.ProcessesStopped = true;
            }

            await CompleteOutputAsync(allocation);
            if (!allocation.OutputDisposed)
            {
                lock (allocation.Output)
                {
                    allocation.OutputClosed = true;
                    allocation.Output.Dispose();
                    allocation.OutputDisposed = true;
                }
            }

            if (!allocation.StandardOutputDisposed)
            {
                allocation.Process.StandardOutput.Dispose();
                allocation.StandardOutputDisposed = true;
            }

            if (!allocation.StandardErrorDisposed)
            {
                allocation.Process.StandardError.Dispose();
                allocation.StandardErrorDisposed = true;
            }

            foreach (var recorded in allocation.RecordedProcesses.Values)
            {
                if (recorded.Disposed) continue;
                recorded.Process.Dispose();
                recorded.Disposed = true;
            }

            if (!allocation.ProcessDisposed)
            {
                allocation.Process.Dispose();
                allocation.ProcessDisposed = true;
            }
        }
        catch (Exception error)
        {
            _log.Write("MATCH", $"清理对局进程失败，保留待清理分配（{allocation.Directory}）：{error.Message}");
            throw;
        }
    }

    private static async Task CompleteOutputAsync(Allocation allocation)
    {
        if (allocation.OutputFailureObserved) return;

        var completion = Task.WhenAll(allocation.OutputTask, allocation.ErrorTask);

        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Completed output tasks do not restart on retry. Report the first failure; retries only close resources still held.
            // Do not skip tasks that timed out but remain active; they could access output objects after shutdown.
            allocation.OutputFailureObserved = completion.IsCompleted;
            throw;
        }
    }

    private static void CaptureRecordedProcesses(Allocation allocation)
    {
        foreach (var fileName in new[] { "afdev.pid", "loader.pid" })
        {
            var path = Path.Combine(allocation.Directory, fileName);
            string content;

            try { content = File.ReadAllText(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            if (!int.TryParse(content.Trim(), out var pid) || pid <= 0)
                throw new InvalidDataException($"子进程 PID 记录无效，不能确认实例已回收：{path}");

            if (allocation.RecordedProcesses.TryGetValue(fileName, out var previous))
            {
                // If the file still contains the same number, trust only the previously retained process object. After it exits, even PID reuse
                // cannot make a new process a leftover of this round.
                if (previous.Id == pid) continue;
                if (!previous.Process.HasExited)
                    throw new IOException($"子进程 PID 记录发生变化，但原进程仍存活，不能放弃原句柄：{path}");

                previous.Process.Dispose();
                allocation.RecordedProcesses.Remove(fileName);
            }

            Process process;

            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { continue; }

            try
            {
                // Acquire and retain the exact kernel handle so retries cannot identify a reused PID as the same process.
                _ = process.SafeHandle;
                if (process.HasExited) continue;

                var startedUtc = process.StartTime.ToUniversalTime();
                var executablePath = process.MainModule?.FileName ?? throw new IOException($"无法读取子进程可执行路径：{path}");

                allocation.RecordedProcesses.Add(fileName, new RecordedProcess(pid, process, executablePath, startedUtc));
                process = null!;
            }
            finally { process?.Dispose(); }
        }
    }

    /// <summary>
    /// Blocks new allocation and reclaims instances individually. Retains failed entries for later
    /// DisposeAsync or ReleaseAsync retries.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposeRequested = true;

            var errors = new List<Exception>();

            foreach (var pair in _allocations.ToArray())
            {
                try
                {
                    await StopAsync(pair.Value);
                    _allocations.Remove(pair.Key);
                }
                catch (Exception error) { errors.Add(error); }
            }

            if (errors.Count != 0) throw new AggregateException("部分对局实例未完成回收，可重试释放。", errors);
            _disposed = true;
        }
        finally { _gate.Release(); }
    }

    private sealed class Allocation(int slot, MatchEndpoint endpoint, Process process, string directory, string statePath, StreamWriter output)
    {
        public int Slot { get; } = slot;
        public MatchEndpoint Endpoint { get; } = endpoint;
        public Process Process { get; } = process;
        public string Directory { get; } = directory;
        public string StatePath { get; } = statePath;
        public StreamWriter Output { get; } = output;
        public bool ReleasePending { get; set; }
        public bool ProcessesStopped { get; set; }
        public bool OutputFailureObserved { get; set; }
        public bool OutputDisposed { get; set; }
        public bool StandardOutputDisposed { get; set; }
        public bool StandardErrorDisposed { get; set; }
        public bool ProcessDisposed { get; set; }
        public Dictionary<string, RecordedProcess> RecordedProcesses { get; } = [];
        public volatile bool OutputClosed;
        public Task OutputTask { get; set; } = Task.CompletedTask;
        public Task ErrorTask { get; set; } = Task.CompletedTask;
    }

    private sealed record RecordedProcess(int Id, Process Process, string ExecutablePath, DateTime StartedUtc)
    {
        public bool Disposed { get; set; }
    }
}
