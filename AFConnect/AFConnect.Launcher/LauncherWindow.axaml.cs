using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AFConnect.EDGE;
using AFConnect.EDGE.Client;

namespace AFConnect.Launcher;

/// <summary>
/// Implements native launcher interaction using actual connection progress. Keeps the proxy and handoff
/// memory alive while the game runs.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly StreamWriter? log;
    private readonly Queue<string> journal = new();
    private readonly object journalGate = new();
    private CancellationTokenSource? operation;
    private ConnectSession? session;
    private bool busy;
    private bool closing;
    private bool canClose;

    /// <summary>Creates the AF2014 launch window without opening listeners or starting the game.</summary>
    public LauncherWindow()
    {
        AvaloniaXamlLoader.Load(this);
        InitializePresentation();

        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "logs");

            Directory.CreateDirectory(directory);
            log = new StreamWriter(Path.Combine(directory, $"launcher-{DateTime.UtcNow:yyyyMMdd}.log"), true) { AutoFlush = true };
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        this.FindControl<Border>("TitleBar")!.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                BeginMoveDrag(e);
        };
        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("SettingsButton")!.Click += async (_, _) =>
        {
            await new SettingsWindow().ShowDialog(this);
            LoadEndpoint();
        };
        this.FindControl<Button>("ToolsButton")!.Click += async (_, _) =>
        {
            if (busy || session is not null) return;

            await new ToolsWindow(WriteLog).ShowDialog(this);
            LoadEndpoint();
        };
        this.FindControl<Button>("PrimaryButton")!.Click += async (_, _) => await RunPrimaryAsync();
        this.FindControl<Button>("LaunchButton")!.Click += async (_, _) => await RunPrimaryAsync();
        this.FindControl<Button>("SequenceCancelButton")!.Click += async (_, _) =>
        {
            if (busy) operation?.Cancel();
            else await DisconnectAsync();
        };
        this.FindControl<Button>("DisconnectButton")!.Click += async (_, _) =>
        {
            if (busy) operation?.Cancel();
            else await DisconnectAsync();
        };
        this.FindControl<Button>("CancelMatchButton")!.Click += async (_, _) =>
        {
            try
            {
                if (session is not null) await session.CancelPreparationAsync(lifetime.Token);
            }
            catch (Exception error) { await ShowErrorAsync(error); }
        };

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            operation?.Dispose();
            lifetime.Dispose();
            lock (journalGate) log?.Dispose();
        };
    }

    private void LoadEndpoint()
    {
        try
        {
            var options = ConnectOptions.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

            Text("EndpointText", $"{options.ServerHost}:{options.ServerPort}");
            Text("PortalEndpoint", $"{options.ServerHost}:{options.ServerPort}");
        }
        catch (Exception error)
        {
            Text("EndpointText", "请检查连接设置");
            Text("StageText", error.Message);
            WriteLog("[配置] " + error.Message);
        }
    }

    private async Task RunPrimaryAsync()
    {
        if (busy || session?.IsGameRunning == true) return;

        busy = true;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        RefreshButtons();

        try
        {
            if (session is null)
            {
                var options = ConnectOptions.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));
                var candidate = new ConnectSession(options, WriteLog);

                session = candidate;
                await Task.Run(() => candidate.InitializeAsync((step, text) =>
                    Dispatcher.UIThread.Post(() => ShowConnectionStage(step, text)), operation.Token));

                var servers = this.FindControl<ComboBox>("ServerList")!;

                servers.ItemsSource = candidate.Servers.Select(server => server.Name).ToArray();
                servers.SelectedIndex = 0;
                Text("IdentityText", "UIN / " + candidate.Uin);
                Text("PortalIdentity", "UIN / " + candidate.Uin);
                Text("PortalHeading", "欢迎回来，" + candidate.Uin);
                Text("RegionCountText", candidate.Servers.Count + " 个");
                Text("WelcomeIdentity", "欢迎 " + candidate.Uin + " 访问");
                Text("OnlineLabel", "● SESSION ONLINE");
                Text("SessionText", "登录验证完成，你的会话已准备就绪。");
                ShowIdentityStage();
                _ = ObserveHostAsync(candidate);
            }
            else if (!portalVisible)
            {
                // Identity confirmation completes only the display handoff. The user must still choose a server on the main page and explicitly start the game.
                await ShowWelcomeAsync(operation.Token);
            }
            else
            {
                int index = this.FindControl<ComboBox>("ServerList")!.SelectedIndex;

                if (index < 0 || index >= session.Servers.Count) throw new InvalidOperationException("请选择区服。");

                Text("StageText", "获取本次票据，直接启动玩家客户端…");
                await Task.Run(() => session.StartGameAsync(session.Servers[index], operation.Token));
                Text("IdentityText", "UIN / " + session.Uin);
                Text("StageText", "游戏进程运行中，连接通道持续保持。");
                Text("SessionText", "请保持启动器运行。退出游戏后可再次启动。\n关闭窗口会最小化，直到游戏退出。");
                _ = ObserveGameAsync(session);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Text("StageText", "已取消连接，可以重试。");
            await DisconnectAsync();
        }
        catch (Exception error)
        {
            if (session?.IsGameRunning != true) await DisconnectAsync();

            await ShowErrorAsync(error);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            busy = false;
            RefreshButtons();
        }
    }

    private async Task ObserveHostAsync(ConnectSession observed)
    {
        try { await observed.WaitForFailureAsync(); }
        catch (Exception error)
        {
            if (!ReferenceEquals(session, observed) || closing || lifetime.IsCancellationRequested) return;

            Text("OnlineLabel", "● INTERRUPTED");
            Text("StageText", "连接异常，请退出游戏后重新连接。");
            await ShowErrorAsync(error);

            if (!observed.IsGameRunning) await DisconnectAsync();
        }
    }

    private async Task ObserveGameAsync(ConnectSession observed)
    {
        try
        {
            await observed.WaitForGameExitAsync(lifetime.Token);

            if (!ReferenceEquals(session, observed)) return;

            WriteLog("[启动] 玩家客户端已退出。");
            Text("StageText", "游戏已退出，可以再次启动。");
            Text("SessionText", "连接保持就绪；再次启动会获取新的登录票据。");
            RefreshButtons();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            RefreshButtons();
            Text("SessionText", "游戏已退出；检查游戏日志后可以再次启动。");
            await ShowErrorAsync(error);
        }
    }

    private async Task DisconnectAsync()
    {
        if (session?.IsGameRunning == true) return;

        ConnectSession? previous = session;

        session = null;
        welcomeCancellation?.Cancel();

        if (previous is not null) await previous.DisposeAsync();

        this.FindControl<ComboBox>("ServerList")!.ItemsSource = null;
        this.FindControl<ProgressBar>("Progress")!.Value = 0;
        Text("IdentityText", "等待核验");
        Text("OnlineLabel", "● OFFLINE");
        ResetPresentation();
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool connected = session is { Servers.Count: > 0 };
        bool running = session?.IsGameRunning == true;
        var primary = this.FindControl<Button>("PrimaryButton")!;
        var disconnect = this.FindControl<Button>("DisconnectButton")!;

        primary.IsEnabled = !busy && !running;
        Text("PrimaryCaption", busy ? "正在核验" : connected ? "登录游戏" : "开始登陆游戏");
        Text("PrimaryEnglish", busy ? "VERIFYING" : connected ? "LOG IN" : "START LOGIN");
        this.FindControl<Button>("LaunchButton")!.IsEnabled = connected && !busy && !running;
        this.FindControl<Button>("LaunchButton")!.Content = busy ? "正在启动…" : running ? "游戏运行中" : "启动游戏    ↗";
        disconnect.Content = busy ? "取消操作" : "断开连接";
        disconnect.IsEnabled = !running && (busy || connected);
        this.FindControl<Button>("SequenceCancelButton")!.IsVisible = !portalVisible && (busy || connected);
        this.FindControl<Button>("SequenceCancelButton")!.Content = busy ? "取消操作" : "断开连接";
        this.FindControl<Button>("SettingsButton")!.IsEnabled = !busy && session is null;
        this.FindControl<Button>("ToolsButton")!.IsEnabled = !busy && session is null;
        this.FindControl<ComboBox>("ServerList")!.IsEnabled = connected && !busy && !running;
        this.FindControl<Button>("CancelMatchButton")!.IsEnabled = running;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (canClose) return;

        e.Cancel = true;

        // Main-window close requests, including those from the taskbar, must not interrupt a configuration commit in a modal maintenance window.
        if (OwnedWindows.OfType<ToolsWindow>().Any(window => window.IsWorking)) return;

        if (session?.IsGameRunning == true)
        {
            Text("StageText", "游戏仍在运行，启动器已最小化。请先退出游戏，再关闭启动器。");
            WindowState = WindowState.Minimized;
            return;
        }

        if (closing) return;

        closing = true;
        operation?.Cancel();

        while (busy) await Task.Delay(50);

        // A close request may race with suspended game creation. Recheck after the operation completes before releasing the newly established proxy and shared memory.
        if (session?.IsGameRunning == true)
        {
            closing = false;
            Text("StageText", "游戏仍在运行，启动器已最小化。请先退出游戏，再关闭启动器。");
            WindowState = WindowState.Minimized;
            return;
        }

        try
        {
            await DisconnectAsync();
            canClose = true;
            Close();
        }
        catch (Exception error)
        {
            closing = false;
            await ShowErrorAsync(error);
        }
    }

    private async Task ShowErrorAsync(Exception error)
    {
        WriteLog("[错误] " + error.Message);
        Text("StageText", "操作未完成，请查看诊断并重试。");
        Text("SessionText", "操作未完成，请查看诊断并重试。");
        this.FindControl<Border>("DiagnosticsPanel")!.IsVisible = true;

        if (closing || lifetime.IsCancellationRequested) return;

        await LauncherDialogs.ShowAsync(this, "AF2014 · 操作未完成", error.Message);
    }

    private void WriteLog(string text)
    {
        string line = $"{DateTimeOffset.Now:HH:mm:ss} {text}";

        lock (journalGate)
        {
            try { log?.WriteLine(line); }
            catch (IOException) { }

            journal.Enqueue(line);

            while (journal.Count > 120) journal.Dequeue();

            string content = string.Join(Environment.NewLine, journal);

            Dispatcher.UIThread.Post(() => Text("Journal", content));
        }
    }

    private void Text(string name, string value) => this.FindControl<TextBlock>(name)!.Text = value;
}
