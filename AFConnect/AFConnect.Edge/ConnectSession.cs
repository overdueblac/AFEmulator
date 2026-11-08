using AFConnect.EDGE.Client;

namespace AFConnect.EDGE;

/// <summary>
/// Shares one connection lifetime between desktop and diagnostic entry points. The same session owns the
/// game, shared memory, and proxy.
/// </summary>
public sealed class ConnectSession : IAsyncDisposable
{
    private readonly ConnectOptions options;
    private readonly Action<string> log;
    private readonly ConnectHost host;
    private LauncherIdentity? identity;
    private DirectGameProcess? game;
    private bool disposed;

    /// <summary>
    /// Creates a session without opening listeners. The caller must dispose it asynchronously on failure or
    /// shutdown.
    /// </summary>
    public ConnectSession(ConnectOptions options, Action<string> log)
    {
        this.options = options;
        this.log = log;
        host = new ConnectHost(options, log);
    }

    /// <summary>The real account ID returned by the server, or zero before initialization.</summary>
    public uint Uin => identity?.Uin ?? 0;

    /// <summary>
    /// The actual server directory. Selectable entries become available only after successful
    /// initialization.
    /// </summary>
    public IReadOnlyList<LauncherServer> Servers { get; private set; } = [];

    /// <summary>Whether the owned player game is still running.</summary>
    public bool IsGameRunning => game is { HasExited: false };

    /// <summary>
    /// Validates resources and ports, prepares the shop, and completes server authentication and directory
    /// lookup.
    /// </summary>
    public async Task InitializeAsync(Action<int, string> progress, CancellationToken token)
    {
        progress(1, "校验客户端与公钥");
        await LocalPreflight.CheckAsync(options, token);

        progress(2, "检测本机端口与应用占用");
        PortDiagnostics.Check(options);

        progress(3, "建立服务端安全连接");
        await host.StartAsync(token);

        progress(4, "核对公共商城资源");
        ClientShopCatalog.Apply(options.GamePath, log);

        progress(5, "确认服务端授权身份");
        identity = await LauncherAuthentication.LoginAsync(options, token);
        Servers = await LauncherAuthentication.ReadServersAsync(options, token);

        progress(6, "会话已就绪");
        log($"[连接] 认证成功，UIN={Uin}，可用区服 {Servers.Count} 个。");
    }

    /// <summary>
    /// Monitors proxy failures. The caller must display failures immediately while retaining resources for
    /// any game still running.
    /// </summary>
    public Task WaitForFailureAsync() => host.WaitForFailureAsync();

    /// <summary>
    /// Obtains a fresh launch ticket before directly creating the player executable; never reuses consumed
    /// handoff data.
    /// </summary>
    public async Task StartGameAsync(LauncherServer server, CancellationToken token)
    {
        if (identity is null || !Servers.Contains(server) || IsGameRunning)
            throw new InvalidOperationException("请先连接并选择当前目录中的区服，且只能运行一个玩家客户端。");

        await host.CheckBackendAsync(token);

        LauncherIdentity fresh = await LauncherAuthentication.LoginAsync(options, token);

        identity.Dispose();
        identity = fresh;
        token.ThrowIfCancellationRequested();
        game?.Dispose();
        game = null;
        game = DirectGameProcess.Start(options.GamePath, identity, server, log);
    }

    /// <summary>Waits for this game to exit. The session must remain alive during the wait.</summary>
    public async Task WaitForGameExitAsync(CancellationToken token)
    {
        DirectGameProcess observed = game ?? throw new InvalidOperationException("尚未启动游戏。");

        await observed.WaitForExitAsync(token);

        if (observed.ExitCode != 0)
            throw new IOException($"玩家客户端异常退出（退出码 {observed.ExitCode}）。请查看游戏日志，确认显卡与运行环境后重试。");
    }

    /// <summary>
    /// Cancels current preparation through the existing ZONE control channel without guessing another
    /// account or round.
    /// </summary>
    public Task CancelPreparationAsync(CancellationToken token) => host.CancelPreparationAsync(token);

    /// <summary>
    /// Releases the proxy and memory. Refuses disposal while the game is running to preserve the session
    /// lifetime.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        if (IsGameRunning) throw new InvalidOperationException("请先退出游戏，再关闭连接端。");

        disposed = true;
        await host.DisposeAsync();
        game?.Dispose();
        identity?.Dispose();
    }
}
