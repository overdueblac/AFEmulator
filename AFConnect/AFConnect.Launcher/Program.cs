using Avalonia;
using AFConnect.EDGE;
using AFConnect.EDGE.Client;

namespace AFConnect.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 0) return RunDiagnosticsAsync(args).GetAwaiter().GetResult();

        return AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> RunDiagnosticsAsync(string[] args)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "logs");

        Directory.CreateDirectory(directory);

        using var output = new StreamWriter(Path.Combine(directory, "launcher-check.log"), false) { AutoFlush = true };

        try
        {
            if (args.SequenceEqual(["--help"]))
            {
                output.WriteLine("AF2014 启动器：无参数打开 GUI；--check 校验连接、身份和目录；--prepare-shop 核对商城资源。");
                return 0;
            }

            if (args.Length != 1 || args[0] is not ("--check" or "--prepare-shop")) return 2;

            var options = ConnectOptions.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            if (args[0] == "--prepare-shop")
            {
                await LocalPreflight.CheckAsync(options, timeout.Token);
                ClientShopCatalog.Apply(options.GamePath, output.WriteLine);
                return 0;
            }

            await using var session = new ConnectSession(options, output.WriteLine);

            await session.InitializeAsync((step, text) => output.WriteLine($"{step}/6 {text}"), timeout.Token);
            output.WriteLine($"PASS UIN={session.Uin}; servers={session.Servers.Count}; TCLS directory is not used.");
            return 0;
        }
        catch (Exception error)
        {
            output.WriteLine(error.ToString());
            return 1;
        }
    }
}
