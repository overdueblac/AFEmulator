using System.Security.Cryptography;
using System.Text;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Checks the AFDEV control plane's game-ticket public/private-key pairing without accessing or modifying
/// the client, Proxy, or old launch gate.
/// </summary>
public static class RemoteAuthenticationPreflight
{
    public static async Task ValidateAsync(ServerOptions options, CancellationToken cancellationToken)
    {
        if (!options.RemoteConnect.Enabled)
            throw new InvalidDataException("AFDEV 需要已启用的认证 CONNECT 接入。");

        var expected = options.RemoteConnect.ExpectedApClientSha256;

        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("必须配置 RemoteConnect.ExpectedApClientSha256。");

        using var key = RSA.Create();

        key.ImportFromPem(await File.ReadAllTextAsync(options.PrivateKeyPath, cancellationToken));

        var publicPem = Encoding.ASCII.GetBytes(key.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n") + "\n");

        if (key.KeySize != 1024 || !Convert.ToHexString(SHA256.HashData(publicPem)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("游戏认证私钥与已部署客户端的公钥指纹不匹配。");
    }
}
