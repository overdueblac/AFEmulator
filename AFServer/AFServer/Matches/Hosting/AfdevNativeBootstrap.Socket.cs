using System.Buffers.Binary;
using System.Net;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevNativeBootstrap
{
    private uint socketQueryPage;

    /// <summary>
    /// Queries a live WinSock handle on a separately owned thread, avoiding repeated interruption of the
    /// game main thread.
    /// </summary>
    /// <remarks>
    /// The host serializes calls. Reuse pages only after normal remote-thread exit and successful output
    /// validation; any failure invalidates this native call. On timeout or cancellation, retain potentially
    /// executing threads and pages until the owner reclaims the whole Job.
    /// </remarks>
    internal async Task<(uint Handle, int Port, IPAddress Address)> GetSocketPortAsync(uint socket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(!invocationFaulted, "前次原生调用未正常完成，必须先回收本次 Job，不能继续查询 socket。");

        try
        {
            VerifyNoNativeFailure();

            uint handle = ReadUInt32(socket + 0x14);

            Require(ReadUInt32(socket) == 0x01CD4210 && handle != uint.MaxValue,
                "live encrypted socket 类型或 WinSock 句柄无效。");

            if (socketQueryPage == 0) socketQueryPage = Process.AllocateMemory(4096);

            uint entry = socketQueryPage + 0x100;
            var code = new X86Code();

            Process.WriteMemory(socketQueryPage, new byte[16]);
            Process.WriteMemory(socketQueryPage + 0x40, BitConverter.GetBytes(16));
            // The original PE .idata entry at 0x019F68BC is verified as getsockname. x86 stdcall pushes three arguments right to left,
            // and the callee removes those 12 bytes. The thread entry retains EAX as the exit code and uses ret 4 to remove its LPVOID argument.
            // This wrapper preserves nonvolatile registers and performs no OPEN, FName, or other engine operations.
            code.Byte(0x68).UInt32(socketQueryPage + 0x40).Byte(0x68).UInt32(socketQueryPage);
            code.Byte(0x68).UInt32(handle).Hex("FF15").UInt32(0x019F68BC).Hex("C20400");
            Process.WriteMemory(entry, code.ToArray());

            uint result = await Process.CreateRemoteThreadAsync(entry, 0, TimeSpan.FromSeconds(5), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            VerifyNoNativeFailure();

            byte[] address = Process.ReadMemory(socketQueryPage, 16);

            Require(result == 0 && ReadUInt32(socketQueryPage + 0x40) == 16 &&
                BinaryPrimitives.ReadUInt16LittleEndian(address) == 2,
                "live socket getsockname 没有返回完整 IPv4 sockaddr_in。");
            Require(ReadUInt32(socket) == 0x01CD4210 && ReadUInt32(socket + 0x14) == handle,
                "读取实际端口期间 live socket 类型或句柄发生改变。");

            return (handle, BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(2)), new IPAddress(address.AsSpan(4, 4)));
        }
        catch
        {
            invocationFaulted = true;

            throw;
        }
    }
}
