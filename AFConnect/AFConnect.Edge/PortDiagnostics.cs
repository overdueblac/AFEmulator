using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AFConnect.EDGE;

/// <summary>
/// Queries Windows endpoint ownership and reports a process name, PID, and path that the user can verify in
/// Task Manager.
/// </summary>
public static class PortDiagnostics
{
    /// <summary>
    /// Probes required ports before connecting remotely. Actual listeners must still handle subsequent
    /// races; probing does not reserve ports.
    /// </summary>
    public static void Check(ConnectOptions options)
    {
        foreach (int port in new[] { 9060, 8000, 9010, options.LocalRolePort, options.LocalZonePort })
            Probe(port, udp: false);

        if (options.MatchTransport == "LegacyUdp")
        {
            for (int index = 0; index < options.UdpPortCount; index++)
                Probe(options.UdpPortBase + index, udp: true);
        }
    }

    /// <summary>
    /// Reports bind failures with the actual port owner. Does not guess an application name if the query
    /// fails or the process has exited.
    /// </summary>
    public static IOException Describe(int port, bool udp, SocketException error)
    {
        string protocol = udp ? "UDP" : "TCP";
        string owners;

        try
        {
            string[] rows = ReadOwners(port, udp).Distinct().ToArray();

            owners = rows.Length == 0
                ? "未查到仍在占用此端口的进程。端口可能属于 Windows 保留范围，或占用进程刚刚退出。"
                : "占用应用：\n" + string.Join("\n", rows);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception)
        {
            owners = "Windows 未能提供端口所有权信息：" + exception.Message;
        }

        return new IOException($"无法监听本地 {protocol} 127.0.0.1:{port}（WinSock {error.NativeErrorCode}）。\n{owners}\n" +
            "请按 Ctrl+Shift+Esc 打开任务管理器，在“详细信息”中按 PID 核对并自行关闭冲突进程，然后重试。", error);
    }

    private static void Probe(int port, bool udp)
    {
        using var socket = new Socket(AddressFamily.InterNetwork,
            udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp);

        socket.ExclusiveAddressUse = true;

        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, port)); }
        catch (SocketException error) { throw Describe(port, udp, error); }
    }

    private static IEnumerable<string> ReadOwners(int port, bool udp)
    {
        foreach (int family in new[] { 2, 23 })
        {
            int rowSize = family == 2 ? udp ? 12 : 24 : udp ? 28 : 56;
            int portOffset = family == 2 ? udp ? 4 : 8 : 20;
            uint size = 0;
            uint result = Query(IntPtr.Zero, ref size, family, udp);

            if (result == 0 && size == 0) continue;
            if (result is not (0 or 122) || size is < 4 or > 16777216)
                throw new IOException($"无法查询 {family} 端点表（{result}）。");

            byte[]? snapshot = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                int capacity = checked((int)size);
                IntPtr buffer = Marshal.AllocHGlobal(capacity);

                try
                {
                    result = Query(buffer, ref size, family, udp);

                    if (result == 122 && size <= 16777216) continue;
                    if (result != 0 || size > capacity) throw new IOException($"读取端点表失败（{result}）。");

                    snapshot = new byte[size];
                    Marshal.Copy(buffer, snapshot, 0, snapshot.Length);
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }

            if (snapshot is null) throw new IOException("网络端点表持续变化，请重试。");

            int count = BinaryPrimitives.ReadInt32LittleEndian(snapshot);

            if (count < 0 || count > (snapshot.Length - 4) / rowSize)
                throw new IOException("网络端点表长度无效。");

            for (int index = 0; index < count; index++)
            {
                int offset = 4 + index * rowSize;

                if (BinaryPrimitives.ReadUInt16BigEndian(snapshot.AsSpan(offset + portOffset, 2)) != port) continue;

                int pid = BinaryPrimitives.ReadInt32LittleEndian(snapshot.AsSpan(offset + rowSize - 4));
                string name = pid == 4 ? "System" : "无法读取名称";
                string path = "路径不可读取或进程已经退出";

                try
                {
                    using var process = Process.GetProcessById(pid);

                    name = process.ProcessName + ".exe";
                    path = process.MainModule?.FileName ?? path;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }

                yield return $"{name} · PID {pid}\n{path}";
            }
        }
    }

    private static uint Query(IntPtr buffer, ref uint size, int family, bool udp) => udp
        ? GetExtendedUdpTable(buffer, ref size, false, family, 1, 0)
        : GetExtendedTcpTable(buffer, ref size, false, family, 5, 0);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
