using Avalonia;
using AFServer.Desktop;

namespace AFServer.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (!args.Any(argument => argument is "--check" or "--help" or "--self-test")
                || args.Any(argument => argument is not ("--check" or "--help" or "--self-test" or "--no-pause")))
                return 2;

            return DesktopDiagnostics.RunAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
