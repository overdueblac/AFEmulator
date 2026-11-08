using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AFConnect.Proxy;
using AFServer.AssaultFire.Protocol;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Each mutually authenticated TLS stream owns one game connection and initial key. ROLE and ZONE are never
/// merged by UIN.
/// </summary>
public sealed class RemoteConnectService : IDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly byte[][] allowedPins;
    private readonly uint activeUin;
    public IPAddress ListenAddress { get; }
    public int Port { get; }

    public RemoteConnectService(RemoteConnectOptions options, uint activeUin)
    {
        this.activeUin = activeUin;
        if (!IPAddress.TryParse(options.ListenAddress, out var address) || options.Port is < 1 or > 65535)
            throw new InvalidDataException("RemoteConnect listen address or port is invalid.");
        ListenAddress = address;
        Port = options.Port;
        if (options.AllowedClientCertificateSha256 is not { Length: > 0 })
            throw new InvalidDataException("RemoteConnect requires at least one pinned client certificate.");
        try
        {
            allowedPins = options.AllowedClientCertificateSha256.Select(pin => Convert.FromHexString(pin)).ToArray();
        }
        catch (FormatException) { throw new InvalidDataException("RemoteConnect client certificate pins must be SHA-256 hex strings."); }
        if (allowedPins.Any(pin => pin.Length != 32))
            throw new InvalidDataException("RemoteConnect client certificate pins must contain exactly 64 hex characters.");

        var password = Environment.GetEnvironmentVariable(options.ServerCertificatePasswordEnvironment);

        certificate = X509CertificateLoader.LoadPkcs12FromFile(options.ServerCertificatePath, password,
            X509KeyStorageFlags.UserKeySet);
        if (!certificate.HasPrivateKey || !IsCurrent(certificate))
        {
            certificate.Dispose();
            throw new InvalidDataException("RemoteConnect server certificate must have a private key and be within its validity period.");
        }
    }

    /// <summary>
    /// After mTLS and version negotiation, runs the handler over its exclusive raw-game-stream view,
    /// zeroing handshake data on exit.
    /// </summary>
    public async Task RunAsync(Stream stream, Func<Stream, TunnelRequest, CancellationToken, Task> handler,
        CancellationToken cancellationToken,
        Func<TunnelFramedStream, TunnelRequest, TunnelFrameHandler?>? createAuxiliaryHandler = null)
    {
        using var tls = new SslStream(stream, true, ValidateClientCertificate);
        TunnelRequest? request = null;
        TunnelNegotiation? negotiation = null;

        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false
                }, timeout.Token);

                var handshake = await TunnelWire.ReadHandshakeAsync(tls, timeout.Token);

                request = handshake.Request;
                negotiation = new TunnelNegotiation(handshake.Version,
                    handshake.Version == TunnelNegotiation.FramedVersion ? Guid.NewGuid() : Guid.Empty);
                ValidateRequest(request, activeUin);
                await TunnelWire.WriteResultAsync(tls, negotiation, true, "OK", timeout.Token);
            }
            catch
            {
                if (tls.IsAuthenticated && tls.CanWrite)
                {
                    using var rejectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    rejectionTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        if (negotiation is null)
                            await TunnelWire.WriteResultAsync(tls, false, "Rejected", rejectionTimeout.Token);
                        else
                            await TunnelWire.WriteResultAsync(tls, negotiation, false, "Rejected", rejectionTimeout.Token);
                    }
                    catch { /* peer may have closed the stream */ }
                }
                throw;
            }
            if (negotiation.Version == TunnelNegotiation.FramedVersion)
            {
                TunnelFrameHandler? auxiliary = null;
                await using var framed = new TunnelFramedStream(tls, negotiation,
                    (kind, payload, token) => auxiliary is null
                        ? ValueTask.FromException(new InvalidDataException("此连接不接受 CONNECT 辅助帧。"))
                        : auxiliary(kind, payload, token), leaveOpen: true);

                // Bind the handler to this TLS object. Reject match control and datagrams until game authentication completes.
                auxiliary = createAuxiliaryHandler?.Invoke(framed, request);

                await handler(framed, request, cancellationToken);
            }
            else
            {
                await handler(tls, request, cancellationToken);
            }
        }
        finally
        {
            if (request is not null)
            {
                CryptographicOperations.ZeroMemory(request.Key);
                CryptographicOperations.ZeroMemory(request.Ticket);
            }
        }
    }

    public static void ValidateRequest(TunnelRequest request, uint activeUin)
    {
        if (request.Port is not (8000 or 9010 or 9060 or 65005 or 65006))
            throw new InvalidDataException("Unsupported Connect service port.");

        bool hasAuthentication = request.Key.Length != 0 || request.Ticket.Length != 0;

        if (request.Port is 8000 or 9010 or 9060)
        {
            if (hasAuthentication || request.Uin != 0 || request.AuthType != 0 || request.ServiceId != 0 || request.Mode != 0)
                throw new InvalidDataException("Launcher tunnels cannot carry game authentication.");
            return;
        }
        if (!hasAuthentication)
        {
            if (request.Port is not (65005 or 65006) || request.Mode != 0 || request.AdvertisedMode != 0 ||
                request.AuthType != 0 || request.ServiceId != 0 || (request.Uin != 0 && request.Uin != activeUin))
                throw new InvalidDataException("Invalid TACC tunnel.");
            return;
        }
        if (request.Uin != activeUin || request.Key.Length != 16 || request.Ticket.Length < 8 || request.Mode is not (3 or 4) ||
            request.AdvertisedMode == 0)
            throw new InvalidDataException("Connect game authentication is invalid for this server account.");
    }

    public static TGameCryptoState ResolveAuthentication(TunnelRequest request, TGameAuth auth)
    {
        if (request.Key.Length != 16 || request.Mode is not (3 or 4) || request.AdvertisedMode != auth.AdvertisedMode || request.Uin != auth.Uin ||
            request.AuthType != auth.AuthType || request.ServiceId != auth.ServiceId ||
            !CryptographicOperations.FixedTimeEquals(request.Ticket, auth.Ticket))
            throw new InvalidDataException("Connect tunnel authentication does not match the game connection.");
        return new TGameCryptoState((byte[])request.Key.Clone(), request.Mode);
    }

    public void ValidateTacc(TunnelRequest request, uint uin)
    {
        if (request.Port is not (65005 or 65006) || request.Key.Length != 0 || request.Ticket.Length != 0 ||
            uin != activeUin || (request.Uin != 0 && request.Uin != uin))
            throw new InvalidDataException("Connect TACC tunnel does not match the game connection.");
    }

    private bool ValidateClientCertificate(object sender, X509Certificate? clientCertificate, X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (clientCertificate is null) return false;

        using var client = new X509Certificate2(clientCertificate);

        if (!IsCurrent(client)) return false;

        var digest = client.GetCertHash(HashAlgorithmName.SHA256);

        return allowedPins.Any(pin => CryptographicOperations.FixedTimeEquals(pin, digest));
    }

    private static bool IsCurrent(X509Certificate2 value) =>
        value.NotBefore.ToUniversalTime() <= DateTime.UtcNow && DateTime.UtcNow <= value.NotAfter.ToUniversalTime();

    public void Dispose() => certificate.Dispose();
}
