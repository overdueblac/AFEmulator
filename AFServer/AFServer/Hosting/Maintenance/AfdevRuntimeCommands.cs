using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Built-in AFDEV deployment and native-runtime validation commands, independent of MongoDB, certificates,
/// and legacy-client preflight.
/// </summary>
public static class AfdevRuntimeCommands
{
    /// <summary>Argument help for deployment and native-runtime validation commands.</summary>
    public const string Help = "AFDEV：--create-afdev-manifest 运行版本 原始游戏SHA256 [模式ID 地图ID]；" +
        "--check-afdev-runtime [模式ID 地图ID]；--prepare-afdev-instance allocationID 房间ID 槽号 [模式ID 地图ID]；" +
        "--run-afdev-instance allocationID 房间ID 槽号 就绪保持秒数 [模式ID 地图ID]（0 秒等待 Ctrl+C）。";

    /// <summary>Matches only explicit deployment and native-runtime validation commands.</summary>
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is
        "--create-afdev-manifest" or "--check-afdev-runtime" or "--prepare-afdev-instance" or "--run-afdev-instance";

    /// <summary>
    /// Runs deployment or native validation, defaulting to ordinary BIO 0x0204/0x004A. IDs accept decimal
    /// or 0x hexadecimal notation.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, ServerOptions options, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (!IsCommand(args)) throw new ArgumentException(Help);

        var required = args[0] switch
        {
            "--create-afdev-manifest" => 3, "--prepare-afdev-instance" => 4,
            "--run-afdev-instance" => 5, _ => 1
        };

        if (args.Length != required && args.Length != required + 2) throw new ArgumentException(Help);

        var mode = args.Length == required ? 0x0204u : ParseId(args[required]);
        var mapValue = args.Length == required ? 0x004Au : ParseId(args[required + 1]);

        if (mapValue > ushort.MaxValue) throw new ArgumentException("地图 ID 超出协议范围。");

        var runtime = options.MatchRuntime ?? throw new InvalidDataException("MatchRuntime 配置不能为空。");
        var map = (ushort)mapValue;

        runtime.ResolvePaths(AppContext.BaseDirectory);

        if (args[0] == "--create-afdev-manifest")
        {
            var manifest = await AfdevRuntimePreflight.CreateManifestAsync(runtime, args[1], args[2], mode, map, cancellationToken);

            await output.WriteLineAsync($"AFDEV manifest 已生成：{runtime.ManifestPath}；{manifest.Files.Count} 个文件，版本 {manifest.RuntimeVersion}。");
        }
        else if (args[0] == "--check-afdev-runtime")
        {
            var validation = await AfdevRuntimePreflight.ValidateAsync(runtime, mode, map, cancellationToken);

            CheckPorts(runtime, options.ListenAddress);
            await output.WriteLineAsync($"AFDEV 静态预检通过：{validation.Target.MapName}，{validation.Manifest.Files.Count} 个文件；端口当前可绑定。");
        }
        else
        {
            if (!Guid.TryParse(args[1], out var allocationId) || allocationId == Guid.Empty ||
                !ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var roomId) || roomId == 0 ||
                !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot >= runtime.MaxInstances)
                throw new ArgumentException(Help);

            if (args[0] == "--run-afdev-instance")
            {
                if (!uint.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 86400)
                    throw new ArgumentException("就绪保持秒数必须为 0–86400；0 表示等待 Ctrl+C。");

                return await RunNativeAsync(runtime, allocationId, roomId, slot, mode, map,
                    TimeSpan.FromSeconds(seconds), output, cancellationToken);
            }

            var validation = await AfdevRuntimePreflight.ValidateAsync(runtime, mode, map, cancellationToken);
            var instance = await AfdevInstanceWorkspace.PrepareAsync(validation, allocationId, roomId, slot, cancellationToken);

            await output.WriteLineAsync($"AFDEV 独立目录已准备：{instance.Directory}；私有复制 {instance.PrivateResourceBytes} 字节，共享静态包 {instance.SharedResourceBytes} 字节；候选端口 {instance.PublicPort}/{instance.TargetPort}。");
        }

        await output.WriteLineAsync("本命令只完成部署验证或准备；AFDEV 进程、地图加载、zero-DS-key 和 SESSION_READY 尚需运行期验收。");

        return 0;
    }

    private static async Task<int> RunNativeAsync(MatchRuntimeOptions runtime, Guid allocationId, ulong roomId,
        int slot, uint mode, ushort map, TimeSpan holdDuration, TextWriter output, CancellationToken cancellationToken)
    {
        var validation = await AfdevRuntimePreflight.ValidateAsync(runtime, mode, map, cancellationToken);
        var host = new AfdevProcessHost(validation, allocationId, roomId, slot, output.WriteLine);

        try
        {
            await output.WriteLineAsync($"AFDEV 静态预检通过，开始实际原生启动；最大尝试 {runtime.MaxStartupAttempts}（包含首次）。");
            await host.StartAsync(cancellationToken);
            await host.ObserveAsync(holdDuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await output.WriteLineAsync("AFDEV 验收已取消，正在确认所有拥有资源回收。");

            return 2;
        }
        catch (Exception failure)
        {
            await output.WriteLineAsync($"AFDEV 运行包启动验收失败：{failure.Message}");

            return 1;
        }
        finally
        {
            // Retain the host and reservation until cleanup succeeds. Ctrl+C must not cancel cleanup and release the slot prematurely.
            while (true)
            {
                try
                {
                    await host.StopAsync();

                    break;
                }
                catch (Exception failure)
                {
                    await output.WriteLineAsync($"AFDEV 清理待重试，继续保留所有权与槽：{failure.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
        }

        await output.WriteLineAsync($"AFDEV 运行包启动验收通过：严格 SESSION_READY、持续核实和停止均完成；尝试数 {host.AttemptCount}。");

        return 0;
    }

    private static uint ParseId(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        if (!uint.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException("模式/地图 ID 必须是十进制整数或 0x 十六进制整数。");

        return value;
    }

    private static void CheckPorts(MatchRuntimeOptions options, string listenAddress)
    {
        var address = IPAddress.Parse(listenAddress);
        var sockets = new List<Socket>();

        try
        {
            for (var slot = 0; slot < options.MaxInstances; slot++)
            {
                foreach (var endpoint in new[] { new IPEndPoint(address, options.PublicPortBase + slot),
                    new IPEndPoint(IPAddress.Loopback, options.TargetPortBase + slot) })
                {
                    var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                    {
                        ExclusiveAddressUse = true
                    };

                    sockets.Add(socket);

                    try { socket.Bind(endpoint); }
                    catch (SocketException exception)
                    {
                        throw new IOException($"AFDEV 候选 UDP 端口不可绑定：{endpoint}。", exception);
                    }
                }
            }
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }
    }
}
