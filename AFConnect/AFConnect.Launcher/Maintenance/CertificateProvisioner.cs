using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace AFConnect.Launcher.Maintenance;

/// <summary>
/// Issues portable mutual-TLS certificates directly and updates paired configurations without installing
/// certificates, invoking external tools, or touching game keys.
/// </summary>
internal static class CertificateProvisioner
{
    internal static string Generate(string serverDirectory, string connectDirectory, int validityYears)
    {
        if (validityYears is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(validityYears), "有效期必须为 1–10 年。");

        serverDirectory = ResolveDirectory(serverDirectory);
        connectDirectory = ResolveDirectory(connectDirectory);

        if (serverDirectory.Equals(connectDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Server 与 Connect 必须使用不同目录。");

        AssertStopped(serverDirectory, connectDirectory);

        var pair = new ConfigurationPair(serverDirectory, connectDirectory);
        var remote = pair.Server["RemoteConnect"] as JsonObject
            ?? throw new InvalidDataException("服务端配置缺少 RemoteConnect 对象。");
        var messages = new List<string>();
        string? oldPin = ReadPreviousPin(pair.Connect, connectDirectory, messages);
        var pins = ReadPins(remote).Where(pin => !pin.Equals(oldPin, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string serverRelative = $"secrets/connect-server-{stamp}.pfx";
        string connectRelative = $"secrets/connect-client-{stamp}.pfx";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using var server = CreateCertificate("AFConnect Server", "1.3.6.1.5.5.7.3.1", now, validityYears);
        using var connect = CreateCertificate("AFConnect Client", "1.3.6.1.5.5.7.3.2", now, validityYears);

        Export(server, Path.Combine(serverDirectory, serverRelative));
        Export(connect, Path.Combine(connectDirectory, connectRelative));
        pins.Add(Pin(connect));

        var allowed = new JsonArray();

        foreach (string pin in pins) allowed.Add((JsonNode?)JsonValue.Create(pin));

        remote["ServerCertificatePath"] = serverRelative;
        remote["ServerCertificatePasswordEnvironment"] = "";
        remote["AllowedClientCertificateSha256"] = allowed;
        pair.Connect["ServerCertificateSha256"] = Pin(server);
        pair.Connect["ClientCertificatePath"] = connectRelative;
        pair.Connect["ClientCertificatePasswordEnvironment"] = "";

        // Issuance may take time. Recheck process state before committing so processes using old certificates do not receive replacement disk configuration.
        AssertStopped(serverDirectory, connectDirectory);

        var backups = pair.Commit(stamp);

        messages.Add($"完成：两端证书已配对，有效期至 {server.NotAfter:yyyy-MM-dd HH:mm:ss}。");
        messages.Add($"Server PFX：{serverRelative}");
        messages.Add($"Connect PFX：{connectRelative}");
        messages.Add($"Server 配置备份：{backups.Server}");
        messages.Add($"Connect 配置备份：{backups.Connect}");
        messages.Add("PFX 使用空密码并包含私钥，请妥善保管 secrets。旧证书和其他客户端授权已保留。");
        messages.Add("请启动 Server 后重新连接。其他 Connect 副本也需同步新的服务端证书指纹；异机部署请迁移本次配对的配置及证书。");
        return string.Join(Environment.NewLine, messages);
    }

    private static string ResolveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || Path.IsPathRooted(directory))
            throw new InvalidDataException("目录须相对连接端 EXE 填写，例如 ../Server 或 .。");

        return Path.GetFullPath(directory, AppContext.BaseDirectory);
    }

    private static X509Certificate2 CreateCertificate(string name, string usage, DateTimeOffset now, int years)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, true));
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(years));
    }

    private static void Export(X509Certificate2 certificate, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] bytes = certificate.Export(X509ContentType.Pfx, "");

        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            using var check = X509CertificateLoader.LoadPkcs12FromFile(path, "", X509KeyStorageFlags.EphemeralKeySet);

            if (!check.HasPrivateKey || Pin(check) != Pin(certificate))
                throw new CryptographicException("生成的 PFX 校验失败，配置尚未替换。");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Pin(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static string? ReadPreviousPin(JsonObject config, string directory, List<string> messages)
    {
        string? path = config["ClientCertificatePath"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(path)) return null;

        string? variable = config["ClientCertificatePasswordEnvironment"]?.GetValue<string>();
        string? password = string.IsNullOrEmpty(variable) ? null : Environment.GetEnvironmentVariable(variable);

        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.GetFullPath(path, directory), password,
                X509KeyStorageFlags.EphemeralKeySet);

            return Pin(certificate);
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            messages.Add("提示：旧客户端证书无法读取，已保留全部旧授权指纹；如需撤销旧授权，请核对服务端允许列表。");
            return null;
        }
    }

    private static IEnumerable<string> ReadPins(JsonObject remote)
    {
        if (remote["AllowedClientCertificateSha256"] is null) return [];
        if (remote["AllowedClientCertificateSha256"] is not JsonArray values)
            throw new InvalidDataException("AllowedClientCertificateSha256 必须是数组，未修改配置。");

        return values.Select(value => value?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!);
    }

    private static void AssertStopped(string serverDirectory, string connectDirectory)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;

                string name;

                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }

                bool isServer = name is "AFServer.GUI" or "AFServer.Avalonia" or "AFServer";
                bool isConnect = name is "AFConnect" or "AFConnect.GUI";

                if (!isServer && !isConnect) continue;

                string? executable;

                try { executable = process.MainModule?.FileName; }
                catch (InvalidOperationException) { continue; }
                catch (Win32Exception)
                {
                    throw new IOException($"无法确认 {name}（PID {process.Id}）所属目录，请先退出该程序后再生成证书。");
                }

                string expected = isServer ? serverDirectory : connectDirectory;

                if (executable is null || string.Equals(Path.GetDirectoryName(executable), expected.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"请先退出 {name}（PID {process.Id}），再生成配对证书。当前工具窗口可以保持打开。");
            }
        }
    }
}
