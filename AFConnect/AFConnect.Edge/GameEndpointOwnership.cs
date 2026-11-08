using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AFConnect.EDGE;

/// <summary>
/// Uses Windows OWNER_PID tables to verify that the UDP source belongs to this ZONE TCP connection's game
/// process. An IP address and port do not establish identity.
/// </summary>
internal sealed class GameEndpointOwnership : IDisposable
{
    private readonly Process process;

    /// <summary>
    /// Resolves the unique client PID from the reversed complete endpoint pair of an accepted TCP
    /// connection without enumerating other game processes.
    /// </summary>
    internal static int FindTcpOwner(TcpClient connection)
    {
        if (connection.Client.RemoteEndPoint is not IPEndPoint client ||
            connection.Client.LocalEndPoint is not IPEndPoint server ||
            client.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(client.Address))
            throw new InvalidDataException("游戏认证必须来自本机已建立的 IPv4 TCP 连接。");

        int[] owners = ReadRows(udp: false)
            .Where(row => row.Local.Equals(client) && row.Remote!.Equals(server))
            .Select(row => row.Pid).Distinct().ToArray();

        return owners.Length == 1 && owners[0] > 0 ? owners[0]
            : throw new InvalidOperationException("无法唯一确认当前游戏 TCP 连接的所属进程；拒绝使用其他同名进程。");
    }

    internal GameEndpointOwnership(TcpClient connection, int processId)
    {
        process = Process.GetProcessById(processId);
        try
        {
            // Retain this process object's handle. PID reuse after exit cannot restore source eligibility for this connection.
            _ = process.Handle;

            var client = (IPEndPoint)connection.Client.RemoteEndPoint!;
            var server = (IPEndPoint)connection.Client.LocalEndPoint!;

            if (!ReadRows(udp: false).Any(row => row.Pid == processId && row.Local.Equals(client) &&
                row.Remote!.Equals(server)))
                throw new InvalidDataException("ZONE TCP 端点不属于已核实的 TGame 进程。");
        }
        catch { process.Dispose(); throw; }
    }

    internal bool Owns(IPEndPoint endpoint)
    {
        if (process.HasExited || endpoint.Address.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.IsLoopback(endpoint.Address)) return false;
        return ReadRows(udp: true).Any(row => row.Pid == process.Id && row.Local.Port == endpoint.Port &&
            (row.Local.Address.Equals(endpoint.Address) || row.Local.Address.Equals(IPAddress.Any)));
    }

    private static IReadOnlyList<OwnerRow> ReadRows(bool udp)
    {
        uint size = 0;
        uint result = udp ? GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, 1, 0)
            : GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);

        if (result != 122 || size is < 4 or > 16777216)
            throw new IOException("无法取得 Windows 网络端点所有权表大小。");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            uint capacity = size;

            try
            {
                result = udp ? GetExtendedUdpTable(buffer, ref size, false, 2, 1, 0)
                    : GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (result == 122 && size <= 16777216) continue;
                if (result != 0 || size > capacity) throw new IOException("Windows 网络端点所有权查询失败。");

                int count = Marshal.ReadInt32(buffer);
                int rowSize = udp ? 12 : 24;

                if (count < 0 || count > (capacity - 4) / rowSize)
                    throw new InvalidDataException("Windows 网络端点所有权表长度无效。");

                var rows = new List<OwnerRow>(count);

                for (int index = 0; index < count; index++)
                {
                    byte[] row = new byte[rowSize];

                    Marshal.Copy(buffer + 4 + index * rowSize, row, 0, rowSize);

                    int offset = udp ? 0 : 4;
                    var local = new IPEndPoint(new IPAddress(row.AsSpan(offset, 4)),
                        BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(offset + 4, 2)));
                    IPEndPoint? remote = udp ? null : new(new IPAddress(row.AsSpan(12, 4)),
                        BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(16, 2)));
                    int pid = BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(rowSize - 4));

                    rows.Add(new(local, remote, pid));
                }

                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        throw new IOException("Windows 网络端点所有权表持续变化，拒绝绑定。");
    }

    private sealed record OwnerRow(IPEndPoint Local, IPEndPoint? Remote, int Pid);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);

    public void Dispose() => process.Dispose();
}
