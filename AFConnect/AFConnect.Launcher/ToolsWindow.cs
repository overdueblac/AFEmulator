using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AFConnect.Launcher.Maintenance;

namespace AFConnect.Launcher;

/// <summary>
/// Provides certificate pairing and hosts repair in a modal window so local maintenance cannot race with
/// connection work.
/// </summary>
internal sealed class ToolsWindow : Window
{
    private readonly TextBox serverDirectory = new() { Text = "../Server" };
    private readonly TextBox connectDirectory = new() { Text = "." };
    private readonly NumericUpDown validityYears = new() { Minimum = 1, Maximum = 10, Value = 5, FormatString = "0", Width = 100 };
    private readonly Button generate = new() { Content = "生成并配对证书" };
    private readonly Button repair = new() { Content = "修复 hosts 并核验解析" };
    private readonly Button close = new() { Content = "关闭", IsCancel = true };
    private readonly SelectableTextBlock result = new() { Text = "选择需要执行的工具。结果及备份位置会显示在这里。", TextWrapping = TextWrapping.Wrap };
    private readonly Action<string> writeLog;
    private readonly ScrollViewer scroll = new();
    private bool working;

    internal bool IsWorking => working;

    internal ToolsWindow(Action<string> writeLog)
    {
        this.writeLog = writeLog;
        Title = "AF2014 · 连接工具";
        Width = 780;
        Height = 740;
        MinWidth = 640;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(26) };
        var form = new StackPanel { Spacing = 18 };
        var certificates = new StackPanel { Spacing = 10 };
        var hosts = new StackPanel { Spacing = 10 };
        var directories = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        var serverField = Field("Server 目录", serverDirectory);
        var connectField = Field("Connect 目录", connectDirectory);
        var validity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        layout.Children.Add(new TextBlock { Text = "连接工具", FontSize = 26, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        certificates.Children.Add(Heading("生成配对证书"));
        certificates.Children.Add(Note("先退出目标 Server，并断开 Connect 与游戏。目录相对当前连接端 EXE；当前工具窗口可保持打开。异机部署请先在本机准备两份配置。"));
        directories.Children.Add(serverField);
        Grid.SetColumn(connectField, 2);
        directories.Children.Add(connectField);
        certificates.Children.Add(directories);
        validity.Children.Add(new TextBlock { Text = "有效期（年）", VerticalAlignment = VerticalAlignment.Center });
        validity.Children.Add(validityYears);
        generate.Classes.Add("primary");
        validity.Children.Add(generate);
        certificates.Children.Add(validity);
        certificates.Children.Add(Note("将更换两端 TLS 证书及配对指纹，保留旧 PFX 和配置备份。新 PFX 含私钥且为空密码，请妥善保管；其他 Connect 副本也需同步服务端新指纹。"));
        form.Children.Add(Card(certificates));

        hosts.Children.Add(Heading("修复本机 hosts"));
        hosts.Children.Add(Note("将 tversion、tauthproxy、tdir.levelupgames.ph 三个兼容域名映射到 127.0.0.1，备份原文件并保留其他映射、注释和编码，随后刷新 DNS 并核验解析。需要管理员权限。"));
        repair.HorizontalAlignment = HorizontalAlignment.Left;
        hosts.Children.Add(repair);
        form.Children.Add(Card(hosts));
        form.Children.Add(Heading("执行结果"));
        form.Children.Add(new Border
        {
            Background = Brush.Parse("#F6F5F2"), Padding = new Thickness(14), MinHeight = 110,
            Child = result
        });

        scroll.Content = form;
        scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(close, 2);
        layout.Children.Add(close);
        Content = layout;

        generate.Click += async (_, _) =>
        {
            string server = serverDirectory.Text ?? "";
            string connect = connectDirectory.Text ?? "";
            int years = (int)(validityYears.Value ?? 5);

            await RunAsync("生成配对证书", () => Task.Run(() => CertificateProvisioner.Generate(server, connect, years)));
        };
        repair.Click += async (_, _) => await RunAsync("修复 hosts", () => Task.Run(HostsFileRepair.RepairAsync));
        close.Click += (_, _) => Close();
        Closing += (_, args) => args.Cancel = working;
    }

    private async Task RunAsync(string operation, Func<Task<string>> action)
    {
        if (working) return;

        SetWorking(true);
        result.Foreground = Brush.Parse("#111111");
        result.Text = operation + "，请稍候…";

        try
        {
            result.Text = await action();
            writeLog("[工具] " + result.Text);
        }
        catch (Exception error)
        {
            result.Foreground = Brush.Parse("#B54115");
            result.Text = operation + "未完成：" + error.Message
                + (error.InnerException is null ? "" : Environment.NewLine + error.InnerException.Message);
            writeLog("[工具] " + result.Text);
        }
        finally
        {
            SetWorking(false);
            Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void SetWorking(bool value)
    {
        working = value;
        generate.IsEnabled = repair.IsEnabled = close.IsEnabled = !value;
        serverDirectory.IsEnabled = connectDirectory.IsEnabled = validityYears.IsEnabled = !value;
    }

    private static StackPanel Field(string label, Control control)
    {
        var field = new StackPanel { Spacing = 6 };

        field.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        field.Children.Add(control);
        return field;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold };

    private static TextBlock Note(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush.Parse("#68655F")
    };

    private static Border Card(Control content) => new()
    {
        Background = Brush.Parse("#EEECE8"), BorderBrush = Brush.Parse("#BDBAB4"),
        BorderThickness = new Thickness(1), Padding = new Thickness(18), Child = content
    };
}
