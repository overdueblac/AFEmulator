using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AFConnect.Proxy;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Matches;
using AFServer.AssaultFire.Networking;
using AFServer.AssaultFire.Persistence;
using AFServer.AssaultFire.Protocol;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Server composition root. Assembles configuration, storage, protocols, and process management without
/// implementing game business rules.
/// </summary>
public static class ServerApplication
{
    /// <summary>
    /// Shared command-line and desktop host entry point. On cancellation, waits for connections, matches,
    /// and the owned database to shut down in order.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default, RuntimeMonitor? monitor = null)
    {
        if (monitor is null) Console.OutputEncoding = Encoding.UTF8;
        monitor ??= new RuntimeMonitor();
        monitor.ServerState = "启动中";
        monitor.DedicatedState = "待机";
        if (args.Contains("--help"))
        {
            Console.WriteLine("AFServer：无参数启动；--check 检查环境；--self-test 协议回归；--import-legacy <旧服务端目录> 导入旧数据；--create-account 用户名 创建账号；--no-pause 失败时不等待按键。");
            Console.WriteLine(MaintenanceCommands.Help);
            Console.WriteLine("--update-catalog 显式更新 MongoDB 商城目录为发布包 data/catalog.seed.json；不修改玩家数据。");
            Console.WriteLine(AfdevRuntimeCommands.Help);
            Console.WriteLine("--check-native-data 检查发布包内的独立对局数据；退出码 2 表示数据有效但完整对局尚未实现。");
            return 0;
        }
        try
        {
            // Report command-line errors before initializing configuration, certificates, or MongoDB. Normal startup does not probe legacy saves.
            string? legacyDirectory = null;
            int importIndex = Array.IndexOf(args, "--import-legacy");

            if (importIndex >= 0)
            {
                if (args.Length <= importIndex + 1 || args[importIndex + 1].StartsWith("--", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(args[importIndex + 1]))
                    throw new ArgumentException("--import-legacy 必须显式指定旧服务端目录。");
                legacyDirectory = Path.GetFullPath(args[importIndex + 1]);
                if (!Directory.Exists(legacyDirectory)) throw new DirectoryNotFoundException("显式指定的旧服务端目录不存在。");
            }
            if (args.Contains("--self-test"))
            {
                ProtocolCompatibilityChecks.Run();
                Console.WriteLine("协议回归通过。");
                return 0;
            }
            if (args.Contains("--check-native-data"))
            {
                var summary = NativeServerData.Load(AppContext.BaseDirectory).Summary;

                Console.WriteLine($"独立数据有效：地图 {summary.MapCount}，模式 {summary.ModeCount}，规则记录 {summary.RuleAssignmentCount}；未读取客户端文件。");
                Console.WriteLine("完整独立对局：尚未实现；UDP 宿主和消息编解码不能替代世界、复制和战斗逻辑。");
                return summary.IsCompleteGameServer ? 0 : 2;
            }
            if (AfdevRuntimeCommands.IsCommand(args))
            {
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ConsoleCancelEventHandler onCancel = (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };

                Console.CancelKeyPress += onCancel;

                try { return await AfdevRuntimeCommands.RunAsync(args, ServerOptions.Load(AppContext.BaseDirectory), Console.Out, stopping.Token); }
                finally { Console.CancelKeyPress -= onCancel; }
            }
            if (args.Length > 0 && MaintenanceCommands.TryRun(args, ServerOptions.Load(AppContext.BaseDirectory), AppContext.BaseDirectory, Console.Out)) return 0;
            return await RunCoreAsync(args, legacyDirectory, cancellationToken, monitor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            monitor.ServerState = "启动或运行失败";
            // Driver exceptions may contain connection parameters. Avoid printing connection strings or complete exception objects at the top level.
            Console.Error.WriteLine($"启动失败 [{ex.GetType().Name}]。请检查命令参数、MongoDB、配置文件、密钥及端口占用。");
            if (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
                Console.Error.WriteLine(ex.Message);
            if (args.Length == 0 && !Console.IsInputRedirected)
            {
                Console.WriteLine("按任意键关闭窗口。");
                Console.ReadKey(true);
            }
            return 1;
        }
        finally
        {
            monitor.DedicatedStatus = null;
            if (!monitor.ServerState.Contains("失败", StringComparison.Ordinal)) monitor.ServerState = "已停止";
        }
    }

    private static async Task<int> RunCoreAsync(string[] args, string? legacyDirectory,
        CancellationToken cancellationToken, RuntimeMonitor monitor)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var options = ServerOptions.Load(baseDirectory);
        using var remoteConnect = options.RemoteConnect.Enabled
            ? new RemoteConnectService(options.RemoteConnect, options.ActiveUin) : null;
        var gameAddress = options.RemoteConnect.Enabled ? "127.0.0.1" : options.PublicAddress;
        int localRolePort = options.RemoteConnect.Enabled ? options.RemoteConnect.LocalRolePort : 65005;
        ushort localZonePort = checked((ushort)(options.RemoteConnect.Enabled ? options.RemoteConnect.LocalZonePort : 65006));
        var runtime = Path.Combine(baseDirectory, "runtime");

        Directory.CreateDirectory(runtime);

        // The exclusive file-handle lock is thread-independent and safe across await. The OS releases it on abnormal process exit.
        await using var instanceLock = new FileStream(Path.Combine(runtime, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var log = new ServerLog(Path.Combine(baseDirectory, "logs"));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };

        Console.CancelKeyPress += onCancel;
        try
        {
            // Maintenance commands connect only to an existing database; preflight and data maintenance must not create extra processes.
            await using var mongo = new MongoProcessLease(options, monitor);

            await mongo.EnsureReadyAsync(args.All(argument => argument == "--no-pause"), stopping.Token);

            using var database = new MongoDatabaseService(options);

            await database.InitializeAsync(stopping.Token);
            log.Write("DATABASE", $"MongoDB 就绪，数据库 {options.MongoDatabase}");

            var repository = new MongoGameRepository(database);
            var accounts = new MongoAccounts(database);

            if (args.Contains("--update-catalog"))
            {
                if (args.Any(argument => argument is not ("--update-catalog" or "--no-pause")))
                    throw new ArgumentException("--update-catalog 不接受目录路径或其他维护命令。");

                var changed = await repository.UpdateCatalogAsync(Path.Combine(baseDirectory, "data", "catalog.seed.json"), stopping.Token);

                log.Write("CATALOG", changed ? "发布包商城目录已更新到 MongoDB。" : "MongoDB 商城目录已是当前版本。");
                return 0;
            }
            if (Array.IndexOf(args, "--create-account") is var accountIndex && accountIndex >= 0)
            {
                if (accountIndex + 1 >= args.Length) throw new ArgumentException("--create-account 后必须提供用户名。");
                Console.Write("密码（输入不回显）：");

                var password = ReadPassword();
                var uin = await accounts.CreateAsync(args[accountIndex + 1], password, stopping.Token);

                log.Write("ACCOUNT", $"账号创建成功，UIN={uin}。在 appsettings.json 中设置 ActiveUin 后重启。");
                return 0;
            }
            await InitializeDataAsync(options, repository, accounts, legacyDirectory, log, stopping.Token);
            if (legacyDirectory is not null) return 0;

            ClientPreflight? preflight = null;
            AfdevRuntimeValidation? afdevValidation = null;

            if (options.MatchBackend == "Afdev")
            {
                await RemoteAuthenticationPreflight.ValidateAsync(options, stopping.Token);
                afdevValidation = await AfdevRuntimePreflight.ValidateAsync(options.MatchRuntime, 0x0204, 0x004A, stopping.Token);
                log.Write("PREFLIGHT", "AFDEV 完整运行包与游戏认证配对通过；后端显式选择 Afdev。");
            }
            else
            {
                preflight = new ClientPreflight(options, log);
                await preflight.EvaluateAsync(stopping.Token);
            }
            if (args.Contains("--check")) return preflight?.Passed == false ? 2 : 0;
            if (preflight?.Passed == false)
            {
                if (!options.RemoteConnect.Enabled) await preflight.WriteGateAsync(false, CancellationToken.None);
                throw new InvalidOperationException("启动检查未通过，详见 logs 中的检查结果。");
            }

            IMatchServerAllocator allocator = afdevValidation is null
                ? new DedicatedServerAllocator(options, log, options.Matches)
                : new AfdevMatchAllocator(afdevValidation, gameAddress, message => log.Write("AFDEV", message));
            await using var allocatorLifetime = (IAsyncDisposable)allocator;
            monitor.DedicatedStatus = allocator is AfdevMatchAllocator observedAllocator
                ? () => observedAllocator.RuntimeStatus : () => "就绪 · Dedicated 按需分配";
            var dispatcher = new GameDispatcher(repository, allocator, gameAddress, localZonePort,
                protocolLog: message => log.Write("PROTOCOL", message));
            var resolver = options.RemoteConnect.Enabled ? null : new NativeTicketKeyResolver();
            await using var host = new TcpServiceHost(log);

            try
            {
                if (allocator is AfdevMatchAllocator warmAllocator) warmAllocator.StartWarmup();

                async Task HandleGameAsync(string serviceName, Stream stream, CancellationToken token,
                    TunnelRequest? tunnel = null, AfdevSessionTransport? matchTransport = null)
                {
                    GameSession? session = null;

                    try
                    {
                        await TGameConnection.RunAsync(stream, async (auth, ct) =>
                        {
                            log.Write("PROTOCOL", $"RX {serviceName} AUTH_AP UIN={auth.Uin} AuthType={auth.AuthType} mode={auth.AdvertisedMode} service_id={auth.ServiceId} ticket={auth.Ticket.Length}B");

                            var account = await accounts.FindAsync(auth.Uin, ct);

                            if (account is null || account.GetValue("status", "disabled") != "active")
                                throw new InvalidDataException("账号不存在或已停用。");

                            var crypto = tunnel is null ? await (resolver ?? throw new InvalidOperationException("远程游戏认证必须来自 CONNECT 隧道。")).ResolveAsync(auth, ct)
                                : RemoteConnectService.ResolveAuthentication(tunnel, auth);

                            log.Write("PROTOCOL", $"TX {serviceName} AUTH_AP_ACCEPT UIN={auth.Uin} mode={crypto.Mode} result=OK");
                            return crypto;
                        }, async (connection, payload, ct) =>
                        {
                            if (session is null)
                            {
                                session = new GameSession(connection.Auth.Uin, connection.SendAsync,
                                    message => log.Write("PROTOCOL", message));
                                if (serviceName == "ZONE" && matchTransport is not null)
                                {
                                    matchTransport.Bind(session);
                                    session.MatchTransport = matchTransport;
                                }
                                log.Write("SESSION", $"已认证 {serviceName} session={session.SessionId:N} UIN={session.Uin}");
                            }
                            await dispatcher.HandleAsync(session, payload, ct);
                        }, token, authenticateTacc: tunnel is null ? null : uin => remoteConnect!.ValidateTacc(tunnel, uin));
                    }
                    finally
                    {
                        if (session is not null)
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                            await dispatcher.DisconnectAsync(session, cleanup.Token);
                        }
                    }
                }

                Task HandleServiceAsync(int port, Stream stream, CancellationToken token,
                    TunnelRequest? tunnel = null, AfdevSessionTransport? matchTransport = null) => port switch
                {
                    9060 => LauncherConnections.HandleVersionAsync(stream, token, message => log.Write("PROTOCOL", $"VERSION {message}")),
                    8000 => LauncherConnections.HandleAuthAsync(stream, options.PrivateKeyPath, options.ActiveUin, token,
                        message => log.Write("PROTOCOL", $"AUTH {message}")),
                    9010 => LauncherConnections.HandleDirectoryAsync(stream, gameAddress, localRolePort, token,
                        message => log.Write("PROTOCOL", $"DIR {message}")),
                    65005 => HandleGameAsync("ROLE", stream, token, tunnel),
                    65006 => HandleGameAsync("ZONE", stream, token, tunnel, matchTransport),
                    _ => throw new InvalidDataException("Unsupported service port.")
                };
                if (remoteConnect is not null)
                {
                    host.Bind(remoteConnect.ListenAddress, remoteConnect.Port, "CONNECT", async (stream, token) =>
                    {
                        AfdevSessionTransport? matchTransport = null;

                        try
                        {
                            await remoteConnect.RunAsync(stream,
                                (tls, request, ct) => HandleServiceAsync(request.Port, tls, ct, request, matchTransport), token,
                                (framed, request) =>
                                {
                                    if (request.Port != 65006 || allocator is not AfdevMatchAllocator afdev) return null;
                                    matchTransport = new AfdevSessionTransport(framed, afdev, message => log.Write("ADMISSION", message));
                                    return matchTransport.HandleFrameAsync;
                                });
                        }
                        finally
                        {
                            if (matchTransport is not null) await matchTransport.DisposeAsync();
                        }
                    });
                }
                else
                {
                    var address = IPAddress.Parse(options.ListenAddress);

                    foreach (var (port, name) in new[] { (9060, "VERSION"), (8000, "AUTH"), (9010, "DIR"), (65005, "ROLE"), (65006, "ZONE") })
                        host.Bind(address, port, name, (stream, token) => HandleServiceAsync(port, stream, token));
                }
                host.Start();
                if (!options.RemoteConnect.Enabled) await preflight!.WriteGateAsync(true, stopping.Token);
                monitor.ServerState = "运行中";
                log.Write("MAIN", "服务端已启动，等待客户端连接；Ctrl+C 安全停止。");
                try
                {
                    var stopped = Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token);

                    if (await Task.WhenAny(stopped, mongo.Failure) == mongo.Failure)
                        throw await mongo.Failure;

                    await stopped;
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                return 0;
            }
            finally
            {
                monitor.ServerState = "安全停止中";
                try
                {
                    if (!options.RemoteConnect.Enabled) await preflight!.WriteGateAsync(false, CancellationToken.None);
                }
                finally
                {
                    try { await host.DisposeAsync(); }
                    finally
                    {
                        // Retain the same host and reservation for cleanup retries. Release the database lease last, outside this scope.
                        await DrainMatchesAsync(dispatcher, log);
                        await DrainAllocatorAsync(allocatorLifetime, log);
                        monitor.DedicatedStatus = null;
                        monitor.DedicatedState = "已停止";
                    }
                }
            }
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static async Task DrainMatchesAsync(GameDispatcher dispatcher, ServerLog log)
    {
        while (true)
        {
            try
            {
                await dispatcher.StopMatchesAsync(CancellationToken.None);
                return;
            }
            catch (Exception failure)
            {
                log.Write("SHUTDOWN", $"对局回收未完成 [{failure.GetType().Name}]；保留原宿主、资格与端口占用，一秒后在同一对象上重试。");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task DrainAllocatorAsync(IAsyncDisposable allocator, ServerLog log)
    {
        while (true)
        {
            try
            {
                await allocator.DisposeAsync();
                return;
            }
            catch (Exception failure)
            {
                log.Write("SHUTDOWN", $"预热实例回收未完成 [{failure.GetType().Name}]；保留原对象与占用，一秒后重试。");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task InitializeDataAsync(ServerOptions options, MongoGameRepository repository,
        MongoAccounts accounts, string? legacyDirectory, ServerLog log, CancellationToken cancellationToken)
    {
        await repository.ImportCatalogAsync(Path.Combine(AppContext.BaseDirectory, "data", "catalog.seed.json"), cancellationToken);
        if (legacyDirectory is null)
        {
            await accounts.EnsureLocalIdentityAsync(options.ActiveUin, "LocalPlayer", cancellationToken);
            return;
        }

        var nickname = "LocalPlayer";
        var activePath = Path.Combine(legacyDirectory, "account_active.json");

        if (File.Exists(activePath))
        {
            var active = JsonNode.Parse(await File.ReadAllTextAsync(activePath, cancellationToken));

            if (active?["uin"]?.GetValue<uint>() == options.ActiveUin)
                nickname = active["nickname"]?.GetValue<string>() ?? nickname;
        }
        await accounts.EnsureLocalIdentityAsync(options.ActiveUin, nickname, cancellationToken);

        var statePath = Path.Combine(legacyDirectory, "assaultfire_mall_state.json");

        if (File.Exists(statePath) && await repository.LoadPlayerAsync(options.ActiveUin, cancellationToken) is null)
        {
            var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath, cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("旧背包存档不是 JSON 对象，导入已停止。");

            if (state["inventory"] is not JsonArray || state["wallet"] is not JsonObject)
                throw new InvalidDataException("旧背包存档缺少 inventory 或 wallet，导入已停止。");
            state["nickname"] = nickname;
            if (await repository.ImportPlayerAsync(options.ActiveUin, state, cancellationToken))
                log.Write("IMPORT", $"旧角色、钱包和背包已导入 MongoDB，UIN={options.ActiveUin}；原文件保留。");
        }
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

        var buffer = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0) buffer.Length--;
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }
}
