using System.Text.Json;
using System.Text.Json.Nodes;
using AFServer.AssaultFire.Hosting;

namespace AFServer.Desktop;

/// <summary>
/// Windowless preflight starts no database, player client, or DS; writes results under the executable
/// directory.
/// </summary>
internal static class DesktopDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "state", "logs", "desktop-check.json");
        using var capture = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(capture);
            Console.SetError(capture);

            var code = await ServerApplication.RunAsync([.. args, "--no-pause"], monitor: new RuntimeMonitor());
            var report = new JsonObject
            {
                ["ok"] = code == 0, ["exitCode"] = code, ["output"] = capture.ToString(),
                ["interfaceConstructed"] = false, ["gameStarted"] = false, ["dsStarted"] = false,
                ["databaseStarted"] = false
            };

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, DesktopJsonContext.Indented.JsonObject));
            return code;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
