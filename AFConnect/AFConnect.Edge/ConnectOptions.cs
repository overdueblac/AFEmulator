using System.Text.Json;

namespace AFConnect.EDGE;

/// <summary>
/// Connect deployment settings. Resolve every relative path against the Connect executable directory.
/// </summary>
public sealed class ConnectOptions
{
    public string GamePath { get; set; } = "../AF_2014";
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 9443;
    /// <summary>
    /// The local ROLE listener port. Must match RemoteConnect.LocalRolePort on Server; the tunnel service
    /// number remains 65005.
    /// </summary>
    public int LocalRolePort { get; set; } = 65005;
    /// <summary>
    /// The local ZONE listener port. Must match RemoteConnect.LocalZonePort on Server; the tunnel service
    /// number remains 65006.
    /// </summary>
    public int LocalZonePort { get; set; } = 65006;
    /// <summary>
    /// Explicitly selects the CONNECT version. Version 1 preserves legacy-server compatibility; version 2
    /// enables game framing without automatic downgrade.
    /// </summary>
    public int TunnelVersion { get; set; } = 1;
    /// <summary>
    /// Explicitly selects LegacyUdp forwarding or the dynamic authenticated Afdev relay, which requires
    /// CONNECT v2.
    /// </summary>
    public string MatchTransport { get; set; } = "LegacyUdp";
    public string ServerCertificateSha256 { get; set; } = "";
    public string ClientCertificatePath { get; set; } = "secrets/connect.pfx";
    public string ClientCertificatePasswordEnvironment { get; set; } = "AF_EDGE_CERT_PASSWORD";
    public string GamePublicKeySha256 { get; set; } = "";
    /// <summary>
    /// The launcher-authentication public key. Resolve relative paths against the Connect executable
    /// directory without relying on client TCLS files.
    /// </summary>
    public string LauncherPublicKeyPath { get; set; } = "secrets/APClient.dat";
    public bool LaunchGame { get; set; } = true;
    public int UdpPortBase { get; set; } = 65008;
    public int UdpPortCount { get; set; } = 4;

    /// <summary>
    /// Loads configuration and validates ports and fingerprints. Resolve both the configuration path and
    /// its file paths against the executable directory.
    /// </summary>
    public static ConnectOptions Load(string path)
    {
        path = Path.GetFullPath(path, AppContext.BaseDirectory);

        ConnectOptions value;

        try
        {
            value = ConnectJson.Serializer.Deserialize<ConnectOptions>(File.ReadAllText(path), new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                ?? throw new InvalidDataException("config.json is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "config.json 格式无效。Windows 路径必须使用正斜杠，例如 D:/Games/AF2014；或把反斜杠写成 D:\\\\Games\\\\AF2014。",
                exception);
        }
        if (string.IsNullOrWhiteSpace(value.GamePath)) throw new InvalidDataException("Set GamePath in config.json.");

        string root = AppContext.BaseDirectory;

        value.GamePath = Path.GetFullPath(value.GamePath, root);
        value.ClientCertificatePath = Path.GetFullPath(value.ClientCertificatePath, root);
        value.LauncherPublicKeyPath = Path.GetFullPath(value.LauncherPublicKeyPath, root);
        if (string.IsNullOrWhiteSpace(value.ServerHost) || value.ServerPort is < 1 or > 65535 ||
            value.UdpPortBase < 1024 || value.UdpPortCount is < 1 or > 64 || value.UdpPortBase + value.UdpPortCount > 65536)
            throw new InvalidDataException("Invalid server or UDP endpoint.");
        if (value.LocalRolePort is < 1024 or > 65535 or 8000 or 9010 or 9060 ||
            value.LocalZonePort is < 1024 or > 65535 or 8000 or 9010 or 9060 ||
            value.LocalRolePort == value.LocalZonePort)
            throw new InvalidDataException("LocalRolePort 与 LocalZonePort 必须是 1024–65535 内的不同端口，且不能占用启动器端口 8000/9010/9060。");
        if (value.TunnelVersion is not (1 or 2))
            throw new InvalidDataException("TunnelVersion 只能为 1 或 2。");
        if (value.MatchTransport is not ("LegacyUdp" or "Afdev") ||
            (value.MatchTransport == "Afdev" && value.TunnelVersion != 2))
            throw new InvalidDataException("MatchTransport 必须为 LegacyUdp 或 Afdev，Afdev 需要 TunnelVersion=2。");
        if (value.ServerCertificateSha256.Length != 64 || !value.ServerCertificateSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Configure ServerCertificateSha256 as 64 hex digits.");
        if (value.GamePublicKeySha256.Length != 0 &&
            (value.GamePublicKeySha256.Length != 64 || !value.GamePublicKeySha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("GamePublicKeySha256 must be empty or 64 hex digits.");
        return value;
    }
}
