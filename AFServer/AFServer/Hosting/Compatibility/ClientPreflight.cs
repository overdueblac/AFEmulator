using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Migrates the original Python launch gate: start the game only after validating the supported client, RSA
/// public key, and local routing.
/// </summary>
public sealed partial class ClientPreflight(ServerOptions options, ServerLog log)
{
    private const string ValidatedTclsHash = "3ff351e0adb594d7544e28db2e966a6d6eb548e9df70daaf4daf58f2ee438d56";
    private static readonly string[] HostNames = ["tversion.levelupgames.ph", "tauthproxy.levelupgames.ph", "tdir.levelupgames.ph"];
    private readonly Dictionary<string, bool> _checks = new();
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, object> _hosts = new();
    private string? _tclsHash;
    public bool Passed => _errors.Count == 0 && _checks.Values.All(value => value);

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (options.RemoteConnect.Enabled)
        {
            _checks["remote_mode"] = true;
            try
            {
                using var privateKey = RSA.Create();

                privateKey.ImportFromPem(await File.ReadAllTextAsync(options.PrivateKeyPath, cancellationToken));

                var parameters = privateKey.ExportParameters(true);

                _checks["rsa_1024"] = privateKey.KeySize == 1024 && parameters.D is { Length: > 0 };

                var expectedPublic = options.RemoteConnect.ExpectedApClientSha256;

                if (expectedPublic.Length != 64 || !expectedPublic.All(Uri.IsHexDigit))
                {
                    _checks["apclient_private_key_match"] = false;
                    _errors.Add("远程模式必须配置 RemoteConnect.ExpectedApClientSha256。");
                }
                else
                {
                    byte[] publicPem = Encoding.ASCII.GetBytes(privateKey.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n") + "\n");

                    _checks["apclient_private_key_match"] = Convert.ToHexString(SHA256.HashData(publicPem)).Equals(expectedPublic, StringComparison.OrdinalIgnoreCase);
                }
                foreach (var component in new[] { parameters.D, parameters.P, parameters.Q, parameters.DP, parameters.DQ, parameters.InverseQ })
                    if (component is not null) CryptographicOperations.ZeroMemory(component);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or ArgumentException)
            { _checks["rsa_1024"] = false; _errors.Add("无法读取远程模式 RSA 私钥。"); }
            foreach (var (name, passed) in _checks)
            {
                log.Write("PREFLIGHT", $"{name}: {(passed ? "PASS" : "FAIL")}");
                if (!passed) _errors.Add($"检查未通过: {name}");
            }
            return;
        }
        _checks["client_root"] = Directory.Exists(options.ClientRoot);

        var tcls = Path.Combine(options.ClientRoot, "TCLS", "Tenio", "TCLS.dll");

        try
        {
            await using var input = File.OpenRead(tcls);

            _tclsHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
            _checks["tcls_validated_build"] = _tclsHash == ValidatedTclsHash;
        }
        catch (IOException) { _checks["tcls_validated_build"] = false; }

        try
        {
            using var privateKey = RSA.Create();

            privateKey.ImportFromPem(await File.ReadAllTextAsync(options.PrivateKeyPath, cancellationToken));

            var publicBytes = await File.ReadAllBytesAsync(Path.Combine(options.ClientRoot, "TCLS", "config", "APClient.dat"), cancellationToken);
            // cryptography emits PEM with LF endings and a newline after END; preserve byte compatibility here.
            var expected = Encoding.ASCII.GetBytes(privateKey.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n") + "\n");

            _checks["apclient_exact_bytes"] = publicBytes.AsSpan().SequenceEqual(expected);

            using var publicKey = RSA.Create();

            publicKey.ImportFromPem(Encoding.ASCII.GetString(publicBytes));
            _checks["same_rsa_key"] = publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(privateKey.ExportSubjectPublicKeyInfo());
            _checks["rsa_1024"] = privateKey.KeySize == 1024;
            _checks["apclient_272_bytes"] = publicBytes.Length == 272;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or ArgumentException)
        {
            _errors.Add("无法验证 RSA 密钥文件；请检查 secrets/PRIVATE.PEM 与客户端 APClient.dat。");
        }
        foreach (var name in new[] { "apclient_exact_bytes", "same_rsa_key", "rsa_1024", "apclient_272_bytes" })
            _checks.TryAdd(name, false);

        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        var hostLines = await File.ReadAllLinesAsync(hostsPath, cancellationToken);
        var hostsOk = true;

        foreach (var name in HostNames)
        {
            var entries = hostLines.Select(line => line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 1 && parts.Skip(1).Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(parts => parts[0]).ToArray();
            string? resolved = null;

            try
            {
                resolved = (await Dns.GetHostAddressesAsync(name, cancellationToken)).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString();
            }
            catch (SocketException) { }
            _hosts[name] = new EvaluateAsyncJson1 { file_values = entries, resolved = resolved };
            hostsOk &= entries is ["127.0.0.1"] && resolved == "127.0.0.1";
        }
        _checks["hosts"] = hostsOk;
        foreach (var (name, passed) in _checks)
        {
            log.Write("PREFLIGHT", $"{name}: {(passed ? "PASS" : "FAIL")}");
            if (!passed) _errors.Add($"检查未通过: {name}");
        }
    }

    /// <summary>
    /// State files carry only interprocess launch-gate signals, not player or business persistence.
    /// </summary>
    public async Task WriteGateAsync(bool ready, CancellationToken cancellationToken)
    {
        var path = Path.Combine(options.ProxyRoot, "runtime", "preflight_status.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = new WriteGateAsyncJson1
        {
            schema = 2,
            checked_at_unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            server_pid = Environment.ProcessId,
            passed = Passed,
            listeners_ready = ready,
            log_written = true,
            launch_ready = Passed && ready,
            checks = _checks,
            client_root = options.ClientRoot,
            tcls_path = Path.Combine(options.ClientRoot, "TCLS", "Tenio", "TCLS.dll"),
            tcls_sha256 = _tclsHash,
            apclient_path = Path.Combine(options.ClientRoot, "TCLS", "config", "APClient.dat"),
            private_key_path = options.PrivateKeyPath,
            server_log_path = log.FilePath,
            hosts = _hosts,
            errors = _errors
        };

        await File.WriteAllTextAsync(path + ".tmp", global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(path + ".tmp", path, true);
        log.Write("PREFLIGHT", $"game launch gate: {(Passed && ready ? "UNLOCKED" : "LOCKED")}");
    }
}
