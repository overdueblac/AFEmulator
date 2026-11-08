using System.Text.Json;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Deployment settings. Relative paths always resolve against the executable directory, regardless of the
/// working directory on double-click launch.
/// </summary>
public sealed partial class ServerOptions
{
    public string MongoConnectionString { get; set; } = "mongodb://127.0.0.1:27017";
    public string MongoDatabase { get; set; } = "assaultfire";
    /// <summary>
    /// Local management settings when no existing database is found. Maintenance preflight does not start
    /// MongoDB automatically.
    /// </summary>
    public MongoLocalProcessOptions MongoLocalProcess { get; set; } = new();
    public string ClientRoot { get; set; } = "../AF_2014";
    public string PrivateKeyPath { get; set; } = "secrets/PRIVATE.PEM";
    public string ProxyRoot { get; set; } = "../Proxy";
    public string ListenAddress { get; set; } = "127.0.0.1";
    public string PublicAddress { get; set; } = "127.0.0.1";
    public uint ActiveUin { get; set; } = 10001;
    /// <summary>
    /// Explicitly selects the Dedicated compatibility backend or Afdev validation backend. Rejects unknown
    /// values without fallback.
    /// </summary>
    public string MatchBackend { get; set; } = "Dedicated";
    public AFServer.AssaultFire.Matches.MatchServerOptions Matches { get; set; } = new();
    public AFServer.AssaultFire.Matches.MatchRuntimeOptions MatchRuntime { get; set; } = new();
    public RemoteConnectOptions RemoteConnect { get; set; } = new();

    public static ServerOptions Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "appsettings.json");
        ServerOptions options;

        if (!File.Exists(path)) options = new ServerOptions();
        else
        {
            try
            {
                options = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Deserialize<ServerOptions>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                }) ?? throw new InvalidDataException("appsettings.json 不能为空。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("appsettings.json 格式无效，请检查路径中的反斜杠是否写成 / 或 \\\\。", exception);
            }
        }
        options.MongoConnectionString = Environment.GetEnvironmentVariable("AF_MONGO_URI") ?? options.MongoConnectionString;
        options.MongoDatabase = Environment.GetEnvironmentVariable("AF_MONGO_DATABASE") ?? options.MongoDatabase;
        if (options.MongoLocalProcess is null) throw new InvalidDataException("MongoLocalProcess 不能为 null。");
        options.MongoLocalProcess.Executable = Environment.GetEnvironmentVariable("AF_MONGO_EXECUTABLE") ?? options.MongoLocalProcess.Executable;
        options.MongoLocalProcess.DataDirectory = Environment.GetEnvironmentVariable("AF_MONGO_DATA") ?? options.MongoLocalProcess.DataDirectory;
        options.MongoLocalProcess.Validate();
        options.ClientRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("AF_CLIENT_ROOT") ?? options.ClientRoot, baseDirectory);
        options.PrivateKeyPath = Path.GetFullPath(Environment.GetEnvironmentVariable("AF_PRIVATE_KEY") ?? options.PrivateKeyPath, baseDirectory);
        options.ProxyRoot = Path.GetFullPath(options.ProxyRoot, baseDirectory);
        if (options.MatchBackend is not ("Dedicated" or "Afdev"))
            throw new InvalidDataException("MatchBackend 必须为 Dedicated 或 Afdev。");
        if (options.MatchBackend == "Afdev" && !options.RemoteConnect.Enabled)
            throw new InvalidDataException("Afdev 后端必须启用 RemoteConnect，经认证 CONNECT v2 交付资格与数据报。");
        if (options.RemoteConnect.Enabled &&
            (options.RemoteConnect.LocalRolePort is < 1024 or > 65535 or 8000 or 9010 or 9060 ||
             options.RemoteConnect.LocalZonePort is < 1024 or > 65535 or 8000 or 9010 or 9060 ||
             options.RemoteConnect.LocalRolePort == options.RemoteConnect.LocalZonePort))
            throw new InvalidDataException("RemoteConnect.LocalRolePort 与 LocalZonePort 必须是 1024–65535 内的不同端口，且不能占用启动器端口 8000/9010/9060。");
        options.RemoteConnect.ServerCertificatePath = Path.GetFullPath(options.RemoteConnect.ServerCertificatePath, baseDirectory);
        if (!System.Net.IPAddress.TryParse(options.ListenAddress, out _) ||
            !System.Net.IPAddress.TryParse(options.PublicAddress, out var advertised) ||
            advertised.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("监听地址和对外地址必须为有效 IP，对外地址必须为 IPv4。");
        if (options.ActiveUin < 10001) throw new InvalidDataException("ActiveUin 必须大于等于 10001。");
        return options;
    }
}

public sealed class RemoteConnectOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9443;
    /// <summary>
    /// The Connect ROLE port advertised to players. Must match AFConnect.LocalRolePort without changing the
    /// CONNECT service number.
    /// </summary>
    public int LocalRolePort { get; set; } = 65005;
    /// <summary>
    /// The Connect ZONE port advertised to players. Must match AFConnect.LocalZonePort without changing the
    /// CONNECT service number.
    /// </summary>
    public int LocalZonePort { get; set; } = 65006;
    public string ServerCertificatePath { get; set; } = "secrets/connect-server.pfx";
    public string ServerCertificatePasswordEnvironment { get; set; } = "AF_CONNECT_CERT_PASSWORD";
    public string[] AllowedClientCertificateSha256 { get; set; } = [];
    public string ExpectedApClientSha256 { get; set; } = "";
}
