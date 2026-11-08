using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Waits for MongoDB before starting business services. Sends normal shutdown only to the mongod created by
/// this run with a matching PID.
/// </summary>
internal sealed class MongoProcessLease : IAsyncDisposable
{
    private readonly ServerOptions options;
    private readonly RuntimeMonitor monitor;
    private readonly IMongoDatabase admin;
    private readonly MongoClient client;
    private readonly CancellationTokenSource observing = new();
    private readonly CancellationTokenSource pumping = new();
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamWriter transcript;
    private readonly string nativeLogPath;
    private readonly object logGate = new();
    private Process? owned;
    private Task? observation;
    private Task[] pumps = [];
    private long lastTotal = -1;
    private bool connected;
    private bool transcriptClosed;

    public MongoProcessLease(ServerOptions options, RuntimeMonitor monitor)
    {
        this.options = options;
        this.monitor = monitor;
        AFServer.AssaultFire.Persistence.MongoAotSerialization.EnsureRegistered();

        var settings = MongoClientSettings.FromConnectionString(options.MongoConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
        settings.ConnectTimeout = TimeSpan.FromSeconds(1);
        settings.SocketTimeout = TimeSpan.FromSeconds(2);
        settings.ApplicationName = "AFServer.Desktop";
        client = new MongoClient(settings);
        admin = client.GetDatabase("admin");
        var directory = Path.Combine(Path.Combine(AppContext.BaseDirectory, "logs"),
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-mongo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        monitor.RunDirectory = directory;
        transcript = new StreamWriter(Path.Combine(directory, "mongodb.log"), append: true) { AutoFlush = true };
        nativeLogPath = Path.Combine(directory, "mongod.log");
    }

    public Task<Exception> Failure => failure.Task;

    public bool OwnsProcess => owned is not null;

    public async Task EnsureReadyAsync(bool allowStart, CancellationToken cancellationToken)
    {
        monitor.DatabaseState = "连接中";

        if (await PingAsync(cancellationToken))
        {
            connected = true;
            monitor.DatabaseState = "在线 · 外部实例";
            Write("[HOST] 已连接既有 MongoDB；退出时保留该实例。日志来自 MongoDB getLog/global。");
            observation = ObserveAsync(observing.Token);
            return;
        }

        if (!allowStart || !options.MongoLocalProcess.AutoStart)
        {
            monitor.DatabaseState = "未连接";
            throw new IOException("MongoDB 未就绪；请启动数据库或启用 MongoLocalProcess.AutoStart。");
        }

        var url = new MongoUrl(options.MongoConnectionString);
        var endpoint = url.Servers.ToArray();
        if (endpoint.Length != 1 || endpoint[0].Host is not ("127.0.0.1" or "localhost")
            || url.Username is not null || url.ReplicaSetName is not null || url.UseTls)
        {
            throw new InvalidOperationException("自动启动仅支持无认证的本机单节点 MongoDB；其它连接请预先启动数据库。");
        }

        // Do not start a second process when connection fails but the port is occupied; authentication errors or an unrelated service do not mean offline.
        using (var probe = new TcpClient())
        {
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, endpoint[0].Port, cancellationToken);
                throw new IOException($"端口 {endpoint[0].Port} 已被占用，但 MongoDB ping 失败；请检查现有实例。");
            }
            catch (SocketException)
            {
            }
        }

        var executable = MongoLocalProcessOptions.Resolve(options.MongoLocalProcess.Executable);
        var dataDirectory = MongoLocalProcessOptions.Resolve(options.MongoLocalProcess.DataDirectory);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("未找到 mongod.exe；请配置 MongoLocalProcess.Executable。", executable);
        }

        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException("MongoDB 数据目录不存在；请在设置中选择既有数据库目录，避免误建空库。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in new[] { "--bind_ip", "127.0.0.1", "--port", endpoint[0].Port.ToString(), "--dbpath", dataDirectory, "--logpath", nativeLogPath, "--logappend" })
        {
            start.ArgumentList.Add(argument);
        }

        Write($"[HOST] 正在启动 MongoDB，数据目录：{dataDirectory}");
        owned = Process.Start(start) ?? throw new IOException("无法创建 mongod 进程。");
        pumps = [PumpAsync(owned.StandardOutput), PumpAsync(owned.StandardError), TailNativeLogAsync()];
        var timer = Stopwatch.StartNew();
        monitor.DatabaseState = "启动中";

        while (timer.Elapsed < TimeSpan.FromSeconds(options.MongoLocalProcess.StartupTimeoutSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owned.HasExited)
            {
                throw new IOException($"MongoDB 启动失败，退出码 {owned.ExitCode}；详见右侧日志。");
            }

            if (await PingAsync(cancellationToken))
            {
                if (!await VerifyOwnershipAsync(cancellationToken))
                {
                    throw new IOException("就绪检查发现数据库 PID 不匹配，拒绝连接到意外实例。");
                }

                monitor.DatabaseState = "在线 · 本次托管";
                connected = true;
                Write($"[HOST] MongoDB 已就绪，PID {owned.Id}；业务服务现在可以启动。");
                observation = ObserveAsync(observing.Token);
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("MongoDB 启动超时；业务服务未启动，请检查数据库日志。");
    }

    private async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception error) when (error is MongoException or TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
    {
        if (owned is null || owned.HasExited)
        {
            return false;
        }

        var endpoint = new MongoUrl(options.MongoConnectionString).Server;
        using var probe = new TcpClient(AddressFamily.InterNetwork);
        await probe.ConnectAsync(IPAddress.Loopback, endpoint.Port, cancellationToken);
        // Use the reversed endpoint four-tuple to resolve the server PID, avoiding Windows serverStatus diagnostic fields that may use local encoding.
        return TcpOwnership.FindPeerProcessId(probe.Client.RemoteEndPoint, probe.Client.LocalEndPoint) == owned.Id && !owned.HasExited;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(pumping.Token) is { } line)
            {
                Write(line);
            }
        }
        catch (OperationCanceledException) when (pumping.IsCancellationRequested)
        {
        }
    }

    private async Task TailNativeLogAsync()
    {
        try
        {
            while (!File.Exists(nativeLogPath))
            {
                if (owned!.HasExited)
                {
                    return;
                }

                await Task.Delay(100, pumping.Token);
            }

            using var stream = new FileStream(nativeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (true)
            {
                while (await reader.ReadLineAsync(pumping.Token) is { } line)
                {
                    Write(line);
                }

                if (owned!.HasExited)
                {
                    return;
                }

                await Task.Delay(100, pumping.Token);
            }
        }
        catch (OperationCanceledException) when (pumping.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        var logWarningShown = false;
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (owned?.HasExited == true)
                {
                    failure.TrySetResult(new IOException($"本次 MongoDB 意外退出，退出码 {owned.ExitCode}。"));
                    monitor.DatabaseState = "意外退出";
                    return;
                }

                if (await PingAsync(cancellationToken))
                {
                    failures = 0;
                    monitor.DatabaseState = owned is null ? "在线 · 外部实例" : "在线 · 本次托管";
                }
                else if (++failures >= 3)
                {
                    monitor.DatabaseState = "连接中断";
                    failure.TrySetResult(new IOException("MongoDB 连续三次连接检查失败，正在关闭业务服务。"));
                    return;
                }

                if (owned is null)
                {
                    try
                    {
                        var log = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("getLog", "global"), cancellationToken: cancellationToken);
                        var total = log["totalLinesWritten"].ToInt64();
                        var lines = log["log"].AsBsonArray;
                        var count = lastTotal < 0 ? Math.Min(80, lines.Count)
                            : total < lastTotal ? lines.Count : (int)Math.Min(lines.Count, total - lastTotal);
                        foreach (var line in lines.Skip(lines.Count - count))
                        {
                            Write(line.AsString);
                        }

                        lastTotal = total;
                    }
                    catch (Exception error) when (error is MongoException or TimeoutException)
                    {
                        if (!logWarningShown)
                        {
                            Write("[HOST] 无法读取外部实例的日志（需要 getLog 权限）：" + error.Message);
                            logWarningShown = true;
                        }
                    }
                }

                await Task.Delay(2000, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure.TrySetResult(new IOException("数据库状态监测失败。", error));
        }
    }

    private void Write(string line)
    {
        lock (logGate)
        {
            if (!transcriptClosed)
            {
                transcript.WriteLine(line);
            }
        }

        monitor.Write(true, line);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop monitoring first without canceling stdout reads; record final flush and exit logs until shutdown completes.
        await observing.CancelAsync();
        if (observation is not null)
        {
            await observation;
        }

        try
        {
            if (owned is not null && !owned.HasExited)
            {
                monitor.DatabaseState = "安全关闭中";
                Write("[HOST] 正在请求本次 MongoDB 刷盘并正常退出…");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.MongoLocalProcess.ShutdownTimeoutSeconds));
                while (!owned.HasExited && !timeout.IsCancellationRequested)
                {
                    try
                    {
                        if (!await VerifyOwnershipAsync(timeout.Token))
                        {
                            throw new IOException("关闭数据库时 PID 不匹配，已拒绝关闭外部实例。");
                        }

                        await admin.RunCommandAsync<BsonDocument>(new BsonDocument("shutdown", 1), cancellationToken: timeout.Token);
                    }
                    catch (Exception error) when (error is MongoException or TimeoutException or SocketException || error is OperationCanceledException && timeout.IsCancellationRequested)
                    {
                        // Successful shutdown makes the server disconnect. Confirm exit through the retained process handle.
                    }

                    await Task.WhenAny(owned.WaitForExitAsync(), Task.Delay(250));
                }

                if (!owned.HasExited)
                {
                    throw new IOException("MongoDB 未在时限内正常退出，已保留进程；请检查日志，未执行强制终止。");
                }
            }

            await Task.WhenAll(pumps);
            monitor.DatabaseState = owned is null ? connected ? "外部实例已保留" : "未连接" : "已停止";
        }
        finally
        {
            await pumping.CancelAsync();
            await Task.WhenAll(pumps);
            owned?.Dispose();
            client.Dispose();
            observing.Dispose();
            pumping.Dispose();
            lock (logGate)
            {
                transcriptClosed = true;
                transcript.Dispose();
            }
        }
    }
}
