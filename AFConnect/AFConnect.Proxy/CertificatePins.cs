using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AFConnect.Proxy;

/// <summary>
/// Pins the complete certificate with SHA-256 and checks its validity period. Self-signed certificates must
/// also match the predistributed fingerprint.
/// </summary>
public static class CertificatePins
{
    public static bool Matches(X509Certificate? certificate, string sha256)
    {
        if (certificate is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return false;
        try
        {
            using X509Certificate2 remote = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            DateTime now = DateTime.UtcNow;

            return now >= remote.NotBefore.ToUniversalTime() && now <= remote.NotAfter.ToUniversalTime() &&
                CryptographicOperations.FixedTimeEquals(remote.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(sha256));
        }
        catch (CryptographicException) { return false; }
    }
}
