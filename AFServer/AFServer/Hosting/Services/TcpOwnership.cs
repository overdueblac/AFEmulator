using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Verifies MongoDB process identity through the reversed loopback TCP four-tuple. A listening port alone
/// does not prove ownership.
/// </summary>
internal static class TcpOwnership
{
    private const uint InsufficientBuffer = 122;
    private const int OwnerPidAll = 5;
    private const int Established = 5;
    private const int MaximumTableBytes = 16 * 1024 * 1024;

    internal static int? FindPeerProcessId(EndPoint? peer, EndPoint? local)
    {
        if (!OperatingSystem.IsWindows() || peer is not IPEndPoint remote || local is not IPEndPoint server
            || !IPAddress.IsLoopback(remote.Address) || remote.AddressFamily != server.AddressFamily)
        {
            return null;
        }

        var ipv6 = remote.AddressFamily == AddressFamily.InterNetworkV6;
        var family = ipv6 ? 23 : 2;
        var rowLength = ipv6 ? 56 : 24;
        uint size = 0;
        var result = GetExtendedTcpTable(0, ref size, false, family, OwnerPidAll, 0);

        if (result is not (0 or InsufficientBuffer))
        {
            return null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size is < 4 or > MaximumTableBytes)
            {
                return null;
            }

            var table = Marshal.AllocHGlobal(checked((int)size));

            try
            {
                var actual = size;
                result = GetExtendedTcpTable(table, ref actual, false, family, OwnerPidAll, 0);

                if (result == InsufficientBuffer)
                {
                    size = actual;
                    continue;
                }

                if (result != 0)
                {
                    return null;
                }

                var count = Marshal.ReadInt32(table);

                if (count < 0 || 4L + (long)rowLength * count > actual)
                {
                    return null;
                }

                for (var index = 0; index < count; index++)
                {
                    var row = table + 4 + index * rowLength;
                    var state = Marshal.ReadInt32(row, ipv6 ? 48 : 0);

                    if (state != Established || Port(row, ipv6 ? 20 : 8) != remote.Port
                        || Port(row, ipv6 ? 44 : 16) != server.Port)
                    {
                        continue;
                    }

                    var source = Address(row, ipv6 ? 0 : 4, ipv6, ipv6 ? 16 : 0);
                    var target = Address(row, ipv6 ? 24 : 12, ipv6, ipv6 ? 40 : 0);

                    if (source.Equals(remote.Address) && target.Equals(server.Address))
                    {
                        var pid = Marshal.ReadInt32(row, ipv6 ? 52 : 20);
                        return pid > 0 ? pid : null;
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        return null;
    }

    private static int Port(nint row, int offset) => Marshal.ReadByte(row, offset) * 256 + Marshal.ReadByte(row, offset + 1);

    private static IPAddress Address(nint row, int offset, bool ipv6, int scopeOffset)
    {
        var bytes = new byte[ipv6 ? 16 : 4];
        Marshal.Copy(row + offset, bytes, 0, bytes.Length);
        return ipv6 ? new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, scopeOffset))) : new IPAddress(bytes);
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
}
