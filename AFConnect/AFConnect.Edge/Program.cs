using AFConnect.EDGE;
using AFConnect.EDGE.Client;

Console.OutputEncoding = System.Text.Encoding.UTF8;
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

try
{
    if (args.Contains("--help"))
    {
        Console.WriteLine("AF2014 Connect --config <config.json> [--check | --prepare-shop | --launch]\n默认配置位于 EXE 旁；桌面用户请运行 GUI 交付的 AFConnect.GUI.exe。");
        return 0;
    }

    int configIndex = Array.IndexOf(args, "--config");
    string config = configIndex < 0 ? Path.Combine(AppContext.BaseDirectory, "config.json")
        : configIndex + 1 < args.Length ? args[configIndex + 1] : throw new ArgumentException("--config 缺少路径。");
    var options = ConnectOptions.Load(config);

    if (args.Contains("--prepare-shop"))
    {
        await LocalPreflight.CheckAsync(options, shutdown.Token);
        ClientShopCatalog.Apply(options.GamePath, Console.WriteLine);
        return 0;
    }

    await using var session = new ConnectSession(options, Console.WriteLine);

    await session.InitializeAsync((step, text) => Console.WriteLine($"[{step}/6] {text}"), shutdown.Token);

    if (args.Contains("--check"))
    {
        Console.WriteLine($"PASS 资源、端口、mTLS、身份 UIN={session.Uin} 及目录协议。");
        return 0;
    }

    if (!args.Contains("--no-launch")) await session.StartGameAsync(session.Servers[0], shutdown.Token);

    try
    {
        Task lifetime = session.IsGameRunning ? session.WaitForGameExitAsync(shutdown.Token)
            : Task.Delay(Timeout.Infinite, shutdown.Token);

        await await Task.WhenAny(lifetime, session.WaitForFailureAsync());
    }
    finally
    {
        if (session.IsGameRunning)
        {
            Console.WriteLine("游戏仍在运行；请先退出游戏，连接端会随后关闭。");
            await session.WaitForGameExitAsync(CancellationToken.None);
        }
    }

    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
