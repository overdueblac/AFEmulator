using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AFServer.AssaultFire.Hosting;
using AFServer.Desktop;

namespace AFServer.Avalonia;

/// <summary>
/// Desktop single-instance control and output redirection. Existing ServerApplication still manages the
/// backend.
/// </summary>
public partial class App : Application
{
    private Mutex? instance;
    private MonitorTextWriter? output;
    private TextWriter? originalOut;
    private TextWriter? originalError;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            instance = new Mutex(true, "Local\\AFServer2014.Desktop", out var first);
            if (!first)
            {
                desktop.MainWindow = Dialogs.Create("MAGI / AF2014", "服务端控制台已经打开，请从任务栏切换到现有窗口。");
                desktop.Exit += (_, _) => instance.Dispose();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var monitor = new RuntimeMonitor();

            originalOut = Console.Out;
            originalError = Console.Error;
            output = new MonitorTextWriter(monitor);
            Console.SetOut(output);
            Console.SetError(output);
            desktop.MainWindow = new ServerWindow(monitor);
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                monitor.Write(false, "[UI] " + e.Exception.Message);
                e.Handled = true;
                _ = Dialogs.ShowAsync(desktop.MainWindow, "MAGI · 操作失败", e.Exception.Message);
            };
            desktop.Exit += (_, _) =>
            {
                output.Flush();
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                output.Dispose();
                instance.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
