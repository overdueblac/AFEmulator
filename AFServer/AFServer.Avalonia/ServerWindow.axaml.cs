using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AFServer.Desktop;
using AFServer.AssaultFire.Hosting;

namespace AFServer.Avalonia;

/// <summary>
/// The UI handles interaction/display only; the original composition root coordinates service lifetime.
/// </summary>
public partial class ServerWindow : Window
{
    private readonly RuntimeMonitor monitor;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Stopwatch uptime = new();
    private readonly string historyPath = Path.Combine(AppContext.BaseDirectory, "state", "desktop-history.jsonl");
    private CancellationTokenSource? runningCancellation;
    private Task<int>? running;
    private bool closing;
    private bool allowClose;
    private bool searchDatabase;
    private long serverLines;
    private long mongoLines;
    private int ticks;

    public ServerWindow() : this(new RuntimeMonitor()) { }

    internal ServerWindow(RuntimeMonitor monitor)
    {
        this.monitor = monitor;
        InitializeComponent();

        var workArea = Screens.Primary?.WorkingArea.Size.ToSize(1 / (Screens.Primary?.Scaling ?? 1)) ?? new Size(1920, 1080);

        Width = Math.Min(Width, workArea.Width - 24);
        Height = Math.Min(Height, workArea.Height - 24);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        ServerLog.GotFocus += (_, _) => searchDatabase = false;
        MongoLog.GotFocus += (_, _) => searchDatabase = true;
        refresh.Tick += Refresh;
        Closed += (_, _) => refresh.Stop();
        AddHandler(PointerPressedEvent, WindowPointerPressed, RoutingStrategies.Bubble);
        ReadSettings();
        ReadHistory();
    }

    private void WindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var point = e.GetPosition(this);
        var left = point.X < 6;
        var right = point.X > Bounds.Width - 6;
        var top = point.Y < 6;
        var bottom = point.Y > Bounds.Height - 6;

        if (WindowState == WindowState.Normal && (left || right || top || bottom))
        {
            var edge = top ? left ? WindowEdge.NorthWest : right ? WindowEdge.NorthEast : WindowEdge.North
                : bottom ? left ? WindowEdge.SouthWest : right ? WindowEdge.SouthEast : WindowEdge.South
                : left ? WindowEdge.West : WindowEdge.East;

            BeginResizeDrag(edge, e);
        }
        else if (point.Y <= 38)
        {
            if (e.ClickCount == 2) MaximizeClick(sender, new RoutedEventArgs());
            else BeginMoveDrag(e);
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        refresh.Start();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (running is { IsCompleted: false } || closing)
        {
            return;
        }

        runningCancellation?.Dispose();
        runningCancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SaveSettingsButton.IsEnabled = false;
        uptime.Restart();
        Record("启动", "正在连接数据库并启动业务服务。");
        monitor.Write(false, "\n> SYSTEM START / " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        running = Task.Run(() => ServerApplication.RunAsync(["--no-pause"], runningCancellation.Token, monitor));

        try
        {
            var exit = await running;
            Record(exit == 0 ? "停止" : "失败", $"服务已结束，退出码 {exit}，本次运行 {uptime.Elapsed:hh\\:mm\\:ss}。");
        }
        catch (Exception error)
        {
            monitor.Write(false, "[HOST] " + error.Message);
            Record("失败", error.Message);
        }
        finally
        {
            uptime.Stop();
            StartButton.IsEnabled = !closing;
            StopButton.IsEnabled = false;
            SaveSettingsButton.IsEnabled = running is not { IsCompleted: false } && !HasConfigurationOverrides();
        }
    }

    private async Task StopAsync()
    {
        if (running is not { IsCompleted: false })
        {
            return;
        }

        StopButton.IsEnabled = false;
        FooterText.Text = "SHUTDOWN / 正在回收业务与 DS，然后关闭本次托管的数据库…";
        await runningCancellation!.CancelAsync();
        try { await running; }
        catch (Exception error) { monitor.Write(false, "[HOST] 停止失败：" + error.Message); }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (closing)
        {
            return;
        }

        closing = true;
        // Exit the current Closing callback before closing; avoid Close reentry even when idle or after failed startup.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        StartButton.IsEnabled = false;
        try
        {
            await StopAsync();
        }
        catch (Exception error)
        {
            Record("关闭异常", error.Message);
        }
        finally
        {
            runningCancellation?.Dispose();
            allowClose = true;
            Close();
        }
    }

    private void Refresh(object? sender, EventArgs e)
    {
        var server = new System.Text.StringBuilder();
        var database = new System.Text.StringBuilder();
        for (var count = 0; count < 500 && monitor.TryRead(out var line); count++)
        {
            if (line.Database)
            {
                database.AppendLine(line.Text);
                mongoLines++;
            }
            else
            {
                server.AppendLine(line.Text);
                serverLines++;
            }
        }

        Append(ServerLog, server.ToString());
        Append(MongoLog, database.ToString());
        ServerBadge.Text = ServerNode.Text = runningCancellation?.IsCancellationRequested == true && running is { IsCompleted: false }
            ? "安全停止中" : monitor.ServerState;
        MongoBadge.Text = MongoNode.Text = monitor.DatabaseState;
        DedicatedNode.Text = monitor.DedicatedState;
        ServerBadge.Foreground = ServerNode.Foreground = StateBrush(ServerBadge.Text);
        MongoBadge.Foreground = MongoNode.Foreground = StateBrush(monitor.DatabaseState);
        DedicatedNode.Foreground = StateBrush(monitor.DedicatedState);
        LogCountText.Text = $"SERVER {serverLines:N0} / MONGO {mongoLines:N0} LINES";

        if (++ticks % 6 == 0)
        {
            ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            using var process = Process.GetCurrentProcess();
            ResourceText.Text = $"PID {Environment.ProcessId}  /  RAM {process.WorkingSet64 / 1048576:N0} MB  /  UP {uptime.Elapsed:hh\\:mm\\:ss}";
            FooterText.Text = $"{monitor.ServerState}  /  DS {monitor.DedicatedState}  /  DATABASE {monitor.DatabaseState}";
        }
    }

    private IBrush StateBrush(string state) => state.Contains("失败") || state.Contains("中断") || state.Contains("意外") || state.Contains("异常")
        ? Brushes.Salmon : state.Contains("运行") || state.Contains("在线") || state.Contains("就绪") || state.Contains("对局")
            ? (IBrush)this.FindResource("Green")! : (IBrush)this.FindResource("Dim")!;

    private void Append(TextBox target, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        // Keep recent logs only in the window. Full logs persist on disk, preventing unbounded control text during long runs.
        if (text.Length > 120000) text = text[^120000..];
        if ((target.Text ?? "").Length + text.Length > 220000)
        {
            var cut = (target.Text ?? "").IndexOf('\n', Math.Max(0, (target.Text ?? "").Length - 100000));
            target.Text = cut >= 0 ? (target.Text ?? "")[(cut + 1)..] : "";
        }

        target.Text += text;
        if (FollowLog.IsChecked == true)
        {
            target.ScrollToLine((target.Text ?? "").Count(character => character == '\n'));
        }
    }

    private void ReadSettings()
    {
        try
        {
            var settings = DesktopSettings.Read();
            ExecutablePath.Text = DesktopSettings.NormalizeMongoPath(settings.MongoLocalProcess.Executable);
            DataPath.Text = DesktopSettings.NormalizeMongoPath(settings.MongoLocalProcess.DataDirectory);
            AutoStartMongo.IsChecked = settings.MongoLocalProcess.AutoStart;
            SaveSettingsButton.IsEnabled = running is not { IsCompleted: false } && !HasConfigurationOverrides();
            SettingsNote.Text = HasConfigurationOverrides()
                ? "数据库路径使用 AF_MONGO_EXECUTABLE / AF_MONGO_DATA 环境覆盖，请在对应来源修改。"
                : "配置位置：" + Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        }
        catch (Exception error)
        {
            SettingsNote.Text = "读取配置失败：" + error.Message;
        }
    }

    private static bool HasConfigurationOverrides() => Environment.GetEnvironmentVariable("AF_MONGO_EXECUTABLE") is not null
        || Environment.GetEnvironmentVariable("AF_MONGO_DATA") is not null;

    private void SaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (running is { IsCompleted: false } || HasConfigurationOverrides())
            {
                return;
            }

            DesktopSettings.SaveMongoPaths((ExecutablePath.Text ?? "").Trim(), (DataPath.Text ?? "").Trim(), AutoStartMongo.IsChecked == true);
            ReadSettings();
            SettingsNote.Text = "配置已保存，下次启动生效。数据库文件保留在原位置。";
            Record("配置", "已保存数据库路径与自动启动选项。");
        }
        catch (Exception error)
        {
            SettingsNote.Text = "保存失败：" + error.Message;
        }
    }

    private async void BrowseExecutableClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 mongod.exe", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("MongoDB 引擎 (mongod.exe)") { Patterns = ["mongod.exe"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();

        if (path is not null) ExecutablePath.Text = DesktopSettings.NormalizeMongoPath(path);
    }

    private async void BrowseDataClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择现有 MongoDB 数据目录（db）", AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();

        if (path is not null) DataPath.Text = DesktopSettings.NormalizeMongoPath(path);
    }

    private async void CheckClick(object? sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        DiagnosticsText.Text = "正在读取数据库响应、端口监听与 DS 文件…";
        try
        {
            var result = await Task.Run(() => DesktopSystemCheck.RunAsync(DesktopSettings.Read(), monitor));
            DiagnosticsText.Text = result;
            Record("检查", result.Replace('\n', ' '));
        }
        catch (Exception error)
        {
            DiagnosticsText.Text = "检查失败：" + error.Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private void Record(string kind, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            File.AppendAllText(historyPath, JsonSerializer.Serialize(new DesktopHistoryEntry(DateTimeOffset.Now, kind, message), DesktopJsonContext.Default.DesktopHistoryEntry) + Environment.NewLine);
            if (new FileInfo(historyPath).Length > 1048576)
            {
                File.WriteAllLines(historyPath, File.ReadLines(historyPath).TakeLast(300).ToArray());
            }

            ReadHistory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            monitor.Write(false, "[UI] 运行历史保存失败：" + error.Message);
        }
    }

    private void ReadHistory()
    {
        if (!File.Exists(historyPath))
        {
            return;
        }

        var lines = new List<string>();
        try
        {
            foreach (var line in File.ReadLines(historyPath).TakeLast(100))
            {
                try
                {
                    using var item = JsonDocument.Parse(line);
                    var value = item.RootElement;
                    lines.Add($"{value.GetProperty("at").GetDateTimeOffset():yyyy-MM-dd HH:mm:ss}  [{value.GetProperty("kind").GetString()}]  {value.GetProperty("message").GetString()}");
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    // A single malformed record from an interrupted write does not prevent reading remaining history.
                }
            }

            HistoryText.Text = string.Join(Environment.NewLine + Environment.NewLine, lines.AsEnumerable().Reverse());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HistoryText.Text = "读取历史失败：" + error.Message;
        }
    }

    private void SearchClick(object? sender, RoutedEventArgs e)
    {
        var term = SearchText.Text;
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        var targets = searchDatabase ? new[] { MongoLog, ServerLog } : new[] { ServerLog, MongoLog };
        foreach (var target in targets)
        {
            var index = (target.Text ?? "").IndexOf(term, Math.Min((target.Text ?? "").Length, target.SelectionStart + Math.Abs(target.SelectionEnd - target.SelectionStart)), StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                index = (target.Text ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase);
            }

            if (index >= 0)
            {
                FollowLog.IsChecked = false;
                target.Focus();
                // Avalonia caret updates collapse selection. Position the caret before setting the match range.
                target.CaretIndex = index;
                target.SelectionStart = index;
                target.SelectionEnd = index + term.Length;

                return;
            }
        }

        FooterText.Text = "未找到关键字：" + term;
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchClick(sender, e);
        }
    }

    private void LogsClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
    }

    private async void StartClick(object? sender, RoutedEventArgs e) => await StartAsync();
    private async void StopClick(object? sender, RoutedEventArgs e) => await StopAsync();
    private void ServerNodeClick(object? sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 0;
        ServerLog.Focus();
    }
    private void MongoNodeClick(object? sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 0;
        MongoLog.Focus();
    }
    private void DedicatedNodeClick(object? sender, RoutedEventArgs e) => CheckClick(sender, e);
    private void ClearClick(object? sender, RoutedEventArgs e)
    {
        ServerLog.Clear();
        MongoLog.Clear();
    }
    private void MinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
