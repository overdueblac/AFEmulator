using System.Net;
using System.Net.NetworkInformation;
using MongoDB.Bson;
using MongoDB.Driver;
using AFServer.AssaultFire.Hosting;

namespace AFServer.Desktop;

/// <summary>
/// Read-only checks of actual MongoDB responses, deployment ports, and engine files, without creating
/// game/database processes.
/// </summary>
internal static class DesktopSystemCheck
{
    public static async Task<string> RunAsync(ServerOptions options, RuntimeMonitor monitor)
    {
        var settings = MongoClientSettings.FromConnectionString(options.MongoConnectionString);

        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
        settings.ConnectTimeout = TimeSpan.FromSeconds(2);
        settings.SocketTimeout = TimeSpan.FromSeconds(2);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new MongoClient(settings);
        var database = "响应正常";

        try
        {
            await client.GetDatabase(options.MongoDatabase).RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1), cancellationToken: timeout.Token);
        }
        catch (Exception error) when (error is MongoException or TimeoutException or OperationCanceledException)
        {
            database = "未连接";
        }

        int[] ports = options.RemoteConnect.Enabled ? [options.RemoteConnect.Port] : [9060, 8000, 9010, 65005, 65006];
        var address = IPAddress.Parse(options.RemoteConnect.Enabled ? options.RemoteConnect.ListenAddress : options.ListenAddress);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        var count = ports.Count(port => listeners.Any(endpoint => endpoint.Port == port &&
            (address.Equals(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))));
        string engine;

        if (options.MatchBackend == "Afdev")
        {
            options.MatchRuntime.ResolvePaths(AppContext.BaseDirectory);

            var executables = options.MatchRuntime.UsesReservedRuntime
                ? Enumerable.Range(0, options.MatchRuntime.MaxInstances).Select(options.MatchRuntime.GetRuntimeExecutable).ToArray()
                : [options.MatchRuntime.AfdevExecutable];
            var existing = executables.Count(File.Exists);

            engine = $"AFDEV 文件 {existing}/{executables.Length}";
        }
        else
        {
            engine = File.Exists(Path.Combine(options.ClientRoot, "Binaries", "Win32", "TGame_AFDEV.exe"))
                ? "Dedicated 文件存在" : "Dedicated 文件缺失";
        }

        return $"MongoDB：{database} / 配置 TCP 监听：{count}/{ports.Length} / {engine}。\n"
            + $"业务宿主：{monitor.ServerState}；对局引擎：{monitor.DedicatedState}。监听存在不代表进程归属；完整资源核验请使用 --check。";
    }
}
