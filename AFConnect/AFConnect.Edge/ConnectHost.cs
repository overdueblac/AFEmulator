using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AFConnect.Proxy;
using AFServer.AssaultFire.Networking;
using AFServer.AssaultFire.Protocol;

namespace AFConnect.EDGE;

/// <summary>
/// Binds each game connection to one bidirectional TLS tunnel and delivers its authentication key only to
/// that tunnel.
/// </summary>
public sealed class ConnectHost(ConnectOptions options, Action<string> log) : IAsyncDisposable
{
    private readonly List<TcpListener> listeners = [];
    private readonly List<UdpForwarder> datagrams = [];
    private readonly List<Task> loops = [];
    private readonly ConcurrentDictionary<long, Task> sessions = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim capacity = new(128, 128);
    private X509Certificate2? certificate;
    private long nextSession;
    private readonly ConcurrentDictionary<AfdevClientTunnel, byte> preparationTunnels = new();

    /// <summary>
    /// Cancels preparation only for the unique current authenticated ZONE connection; refuses to guess when
    /// multiple connections exist.
    /// </summary>
    public async Task CancelPreparationAsync(CancellationToken token)
    {
        var candidates = preparationTunnels.Keys.ToArray();

        if (candidates.Length != 1)
        {
            log("[对局] 当前没有唯一的游戏连接，未发送取消；可在游戏内离房取消。");
            return;
        }
        if (!await candidates[0].CancelPreparationAsync(token))
            log("[对局] 当前没有可取消的准备，或取消请求已经发出。");
    }

    public async Task StartAsync(CancellationToken token)
    {
        PortDiagnostics.Check(options);
        certificate = X509CertificateLoader.LoadPkcs12FromFile(options.ClientCertificatePath,
            Environment.GetEnvironmentVariable(options.ClientCertificatePasswordEnvironment), X509KeyStorageFlags.UserKeySet);
        if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
            throw new InvalidDataException("Client TLS certificate is expired or has no private key.");
        await CheckBackendAsync(token);

        // Local ports can avoid Windows reserved ranges. CONNECT service numbers stay fixed, so authentication and session routing do not change with deployment ports.
        foreach (var (localPort, servicePort) in new[]
        {
            (9060, 9060), (8000, 8000), (9010, 9010),
            (options.LocalRolePort, 65005), (options.LocalZonePort, 65006)
        })
        {
            var listener = StartListener(localPort, servicePort);

            listeners.Add(listener);
            loops.Add(AcceptAsync(listener, servicePort, stopping.Token));
        }
        if (options.MatchTransport == "LegacyUdp")
        {
            IPAddress target = (await Dns.GetHostAddressesAsync(options.ServerHost, token))
                .First(ip => ip.AddressFamily == AddressFamily.InterNetwork);

            for (int index = 0; index < options.UdpPortCount; index++)
            {
                int port = options.UdpPortBase + index;
                var forwarder = new UdpForwarder(new IPEndPoint(IPAddress.Loopback, port), new IPEndPoint(target, port));

                datagrams.Add(forwarder);

                try { loops.Add(forwarder.Start(stopping.Token)); }
                catch (SocketException error) { throw PortDiagnostics.Describe(port, true, error); }
            }
        }
        foreach (Task loop in loops) if (loop.IsFaulted) await loop;

        string ready = $"Ready: TCP 9060/8000/9010; ROLE {options.LocalRolePort} -> 65005; ZONE {options.LocalZonePort} -> 65006; ";

        log(options.MatchTransport == "Afdev"
            ? ready + "AFDEV UDP is allocated by authenticated ZONE control."
            : ready + $"UDP {options.UdpPortBase}-{options.UdpPortBase + options.UdpPortCount - 1} -> {options.ServerHost}");
    }

    private TcpListener StartListener(int localPort, int servicePort)
    {
        var listener = new TcpListener(IPAddress.Loopback, localPort) { ExclusiveAddressUse = true };

        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException exception)
        {
            listener.Stop();

            var diagnostic = PortDiagnostics.Describe(localPort, false, exception);
            string message = diagnostic.Message;

            log(message);
            throw diagnostic;
        }
    }

    public Task WaitForFailureAsync() => WaitLoopAsync();

    private async Task WaitLoopAsync()
    {
        Task completed = await Task.WhenAny(loops);

        await completed;
        if (!stopping.IsCancellationRequested) throw new IOException("A proxy listener stopped unexpectedly.");
    }

    public async Task CheckBackendAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var upstream = new TcpClient { NoDelay = true };
        await using var tls = await OpenTunnelAsync(upstream, new TunnelRequest(9060, 0, 0, 0, [], [], 0), timeout.Token);

        // A normal version-service probe verifies that the remote server has begun processing the game protocol.
        await tls.WriteAsync(new byte[65], timeout.Token);

        byte[] response = new byte[1];

        await tls.ReadExactlyAsync(response, timeout.Token);
    }

    private async Task AcceptAsync(TcpListener listener, int port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await capacity.WaitAsync(token);

            TcpClient client;

            try { client = await listener.AcceptTcpClientAsync(token); }
            catch { capacity.Release(); throw; }

            long id = Interlocked.Increment(ref nextSession);
            Task task = ServeAsync(client, port, token);

            sessions[id] = task;
            _ = ObserveAsync(id, task);
        }
    }

    private async Task ObserveAsync(long id, Task task)
    {
        try { await task; }
        finally { sessions.TryRemove(id, out _); capacity.Release(); }
    }

    private async Task ServeAsync(TcpClient client, int port, CancellationToken token)
    {
        using (client)
        using (var upstream = new TcpClient { NoDelay = true })
        {
            byte[] key = [];
            int gameProcessId = 0;
            string stage = "read-client-handshake";
            string handshakeDetails = "";

            try
            {
                client.NoDelay = true;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                NetworkStream local = client.GetStream();
                byte[] prefix = [];
                var request = new TunnelRequest(port, 0, 0, 0, [], [], 0);

                if (port is 65005 or 65006)
                {
                    stage = "read-client-prefix";
                    prefix = new byte[2];
                    await local.ReadExactlyAsync(prefix, timeout.Token);
                    if (prefix[1] == 0x0c)
                    {
                        byte[] header = new byte[9];

                        stage = "read-client-tacc-header";
                        prefix.CopyTo(header, 0);
                        await local.ReadExactlyAsync(header.AsMemory(2), timeout.Token);

                        uint body = Wire.Read32(header, 5);
                        int head = header[3];

                        handshakeDetails = $" handshake=TACC command=0x{header[2]:X2} headerLength={head} bodyLength={body}";
                        stage = "validate-client-tacc";
                        if (port != 65005 || header[2] != 3 || head < 26 || body > 0x400000)
                            throw new InvalidDataException("Invalid TACC frame.");

                        stage = "read-client-tacc-frame";
                        prefix = new byte[checked(head + (int)body)];
                        header.CopyTo(prefix, 0);
                        await local.ReadExactlyAsync(prefix.AsMemory(9), timeout.Token);
                        request = new TunnelRequest(port, Wire.Read32(prefix, 21), 0, 0, [], [], 0);
                    }
                    else
                    {
                        stage = "read-client-tgame-frame";
                        prefix = await TGameConnection.ReadFrameAsync(local, timeout.Token, prefix);
                        handshakeDetails = $" handshake=TGame marker=0x{prefix[1]:X2} command=0x{prefix[2]:X2} frameLength={prefix.Length}";
                        stage = "parse-client-auth";

                        TGameAuth auth = TGameProtocol.ParseAuth(prefix);

                        stage = "locate-tcp-owner";
                        gameProcessId = FindGameProcess(client);
                        stage = "resolve-authap";

                        var resolver = new NativeTicketKeyResolver(gameProcessId);
                        TGameCryptoState state = await resolver.ResolveAsync(auth, timeout.Token);

                        key = state.Key;
                        request = new TunnelRequest(port, auth.Uin, auth.AuthType, auth.ServiceId, auth.Ticket, key, state.Mode, auth.AdvertisedMode);
                    }
                }

                stage = "bind-game-endpoint";

                using var ownership = options.MatchTransport == "Afdev" && port == 65006 && gameProcessId != 0
                    ? new GameEndpointOwnership(client, gameProcessId) : null;
                await using var afdev = ownership is null ? null : new AfdevClientTunnel(ownership.Owns, log);
                stage = "connect-backend";

                await using var tls = await OpenTunnelAsync(upstream, request, timeout.Token,
                    afdev is null ? null : afdev.HandleAsync);

                if (afdev is not null)
                {
                    afdev.Bind((TunnelFramedStream)tls);

                }

                if (prefix.Length > 0) await tls.WriteAsync(prefix, timeout.Token);
                CryptographicOperations.ZeroMemory(key);
                log($"Connected TCP {port}");
                stage = "relay";
                if (afdev is not null) preparationTunnels.TryAdd(afdev, 0);
                try { await DuplexRelay.RunAsync(local, tls, token); }
                finally { if (afdev is not null) preparationTunnels.TryRemove(afdev, out _); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or AuthenticationException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                // Log only protocol commands and lengths during the handshake. Tailor guidance to local parsing, upstream connection, or authenticated forwarding failures.
                // Do not log frame contents, authentication fields, or raw exceptions that may contain them.
                string nativeError = ex is System.ComponentModel.Win32Exception native ? $" win32={native.NativeErrorCode}" : "";
                string advice = stage switch
                {
                    "read-client-handshake" or "read-client-prefix" or "read-client-tacc-header"
                        or "validate-client-tacc" or "read-client-tacc-frame" or "read-client-tgame-frame" or "parse-client-auth"
                        => "本机客户端握手失败；若此前有 relay 断开，请先查看该时刻的 Server/AFDEV 日志。",
                    "locate-tcp-owner" or "resolve-authap" or "bind-game-endpoint"
                        => "请检查游戏进程、连接归属及本机访问权限。",
                    "connect-backend" => "请检查服务地址、证书配置和 Server 接入日志。",
                    "relay" => "已认证的转发连接中断，请查看该时刻的 Server/AFDEV 日志。",
                    _ => "请根据失败阶段检查 Connect 和 Server 日志。"
                };

                log($"TCP {port} closed: {ex.GetType().Name}; stage={stage} pid={gameProcessId}{nativeError}{handshakeDetails}; {advice}");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    private async Task<Stream> OpenTunnelAsync(TcpClient tcp, TunnelRequest request, CancellationToken token,
        TunnelFrameHandler? auxiliaryHandler = null)
    {
        try { await tcp.ConnectAsync(options.ServerHost, options.ServerPort, token); }
        catch (SocketException exception)
        {
            string message = $"无法连接服务端 TCP {options.ServerHost}:{options.ServerPort}（WinSock={exception.NativeErrorCode}）。请确认 Server 已启动且 CONNECT 地址可达。";

            log(message);
            throw new IOException(message, exception);
        }

        var tls = new SslStream(tcp.GetStream(), false, (_, remote, _, _) =>
        {
            return CertificatePins.Matches(remote, options.ServerCertificateSha256);
        });

        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.ServerHost,
                ClientCertificates = new X509CertificateCollection { certificate ?? throw new InvalidOperationException("Certificate not loaded.") },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
            await TunnelWire.WriteRequestAsync(tls, request, options.TunnelVersion, token);

            var negotiation = await TunnelWire.ReadResultAsync(tls, options.TunnelVersion, token);

            // Each local TCP connection uses only its own negotiated stream ID; never share another stream by process or UIN.
            return negotiation.Version == TunnelNegotiation.FramedVersion
                ? new TunnelFramedStream(tls, negotiation, auxiliaryHandler)
                : tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    private int FindGameProcess(TcpClient connection)
    {
        string expected = Path.Combine(options.GamePath, "Binaries", "Win32", "TGame.exe");
        int owner = GameEndpointOwnership.FindTcpOwner(connection);

        // QueryFullProcessImageName uses a limited-query handle and does not traverse modules in suspended or unrelated processes.
        if (!Client.NativeProcess.IsImage(owner, expected))
            throw new InvalidOperationException("当前 TCP 来源不是配置目录中的 TGame.exe，或无法查询其映像路径。");

        return owner;
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        foreach (var listener in listeners) listener.Stop();
        foreach (var relay in datagrams) await relay.DisposeAsync();
        try { await Task.WhenAll(loops.Concat(sessions.Values)); }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
        certificate?.Dispose();
        stopping.Dispose();
        capacity.Dispose();
    }
}
