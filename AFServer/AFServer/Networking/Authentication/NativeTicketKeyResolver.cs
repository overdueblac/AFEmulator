using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AFServer.AssaultFire.Protocol;

namespace AFServer.AssaultFire.Networking;

/// <summary>
/// Locates AuthAP keys compatibly with the local 2014 client. Reads the ticket object only, validating
/// ticket length, authentication type, and connection encryption state without changing client memory.
/// </summary>
public sealed class NativeTicketKeyResolver
{
    private static readonly ConcurrentDictionary<(int Process, long Started, long Anchor), byte> UsedAnchors = new();
    private static readonly SemaphoreSlim ResolutionLock = new(1, 1);
    private readonly int? processId;

    public NativeTicketKeyResolver(int? processId = null) => this.processId = processId;

    public async ValueTask<TGameCryptoState> ResolveAsync(TGameAuth auth, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地票据定位需要 Windows。");
        if (auth.Ticket.Length < 8) throw new InvalidDataException("票据过短，无法定位认证对象。");

        bool debugPrivilege = TryEnableDebugPrivilege(out int privilegeError);

        await ResolutionLock.WaitAsync(token);
        try
        {
            int processCount = 0;
            int openedCount = 0;
            int accessDeniedCount = 0;
            int lastOpenError = 0;
            var observedModes = new HashSet<int>();

            for (int attempt = 0; attempt < 20; attempt++)
            {
                Process[] processes;

                try
                {
                    processes = processId.HasValue ? [Process.GetProcessById(processId.Value)] : Process.GetProcessesByName("TGame");
                }
                catch (ArgumentException)
                {
                    processes = [];
                }

                processCount = Math.Max(processCount, processes.Length);

                var candidates = new List<Candidate>();

                foreach (var process in processes)
                {
                    using (process)
                    {
                        token.ThrowIfCancellationRequested();

                        using SafeProcessHandle handle = OpenReadableProcess(process.Id, out int openError);

                        if (handle.IsInvalid)
                        {
                            lastOpenError = openError;
                            if (lastOpenError == 5) accessDeniedCount++;
                            continue;
                        }

                        openedCount++;

                        long started;

                        try { started = process.StartTime.ToUniversalTime().Ticks; }
                        catch (System.ComponentModel.Win32Exception) { started = 0; }
                        candidates.AddRange(Scan(handle, process.Id, started, auth, token));
                    }
                }
                // Without an explicit PID, avoid merging matching local tickets from different clients into one session.
                if (candidates.Select(x => x.Process).Distinct().Count() > 1)
                    throw new InvalidOperationException("多个 TGame 进程持有相同票据，请为该连接指定客户端 PID。");

                // The old Python server initially accepted mode 2/3/4 AuthAP candidates, then relied on
                // later handshake support checks. Prefer modes 3/4 supported by the C# encryption layer,
                // avoiding stale GEO objects mistaken for current ROLE/ZONE connections in the same process.
                var usable = candidates.Where(x => x.Mode is 3 or 4).ToList();
                Candidate? candidate = usable.OrderByDescending(x => x.Verified).ThenByDescending(x => x.Anchor).FirstOrDefault();

                if (candidate is not null && UsedAnchors.TryAdd((candidate.Process, candidate.Started, candidate.Anchor), 0))
                    return new(candidate.Key, candidate.Mode);
                foreach (var mode in candidates.Select(x => x.Mode)) observedModes.Add(mode);
                await Task.Delay(50, token);
            }
            if (processCount == 0)
                throw new InvalidOperationException("未发现 TGame.exe 进程；请先让客户端完成登录并点击开始，再等待 ROLE/ZONE 连接。");
            if (openedCount == 0 && accessDeniedCount > 0)
            {
                string privilege = debugPrivilege ? "SeDebugPrivilege 已启用" : $"SeDebugPrivilege 未启用（错误码 {privilegeError}）";

                throw new InvalidOperationException($"无法打开 TGame.exe 进程（ERROR_ACCESS_DENIED，{privilege}）。请让服务端与客户端使用相同权限；若客户端以管理员运行，请右键 Server\\AFServer.exe 选择“以管理员身份运行”。");
            }

            string detail = openedCount > 0
                ? observedModes.Count > 0
                    ? $"已打开 {openedCount} 个 TGame 进程，但 AuthAP 传输模式不受支持（候选模式：{string.Join(",", observedModes.OrderBy(x => x))}）"
                    : $"已打开 {openedCount} 个 TGame 进程，但没有找到匹配的 AuthAP 对象（票据长度 {auth.Ticket.Length}、AuthType {auth.AuthType}、UIN {auth.Uin}）"
                : $"打开 TGame 进程失败，最后错误码 {lastOpenError}";

            throw new InvalidOperationException($"{detail}。请保持客户端登录状态，并确认服务端与客户端使用相同版本和权限。");
        }
        finally { ResolutionLock.Release(); }
    }

    private static IEnumerable<Candidate> Scan(SafeProcessHandle handle, int pid, long started, TGameAuth auth, CancellationToken token)
    {
        var found = new List<Candidate>();
        long address = 0;

        while (address < 0x7fff0000)
        {
            token.ThrowIfCancellationRequested();
            if (VirtualQueryEx(handle, (nint)address, out MemoryInformation info, (nuint)Marshal.SizeOf<MemoryInformation>()) == 0) break;

            long regionSize = checked((long)info.RegionSize), regionBase = info.BaseAddress.ToInt64();

            if (info.State == 0x1000 && info.Type == 0x20000 && (info.Protect & 0x101) == 0 && regionSize <= 0x04000000)
            {
                byte[]? block = Read(handle, regionBase, (int)regionSize);

                if (block is not null)
                {
                    int offset = 0;

                    while (offset <= block.Length - auth.Ticket.Length)
                    {
                        int match = block.AsSpan(offset).IndexOf(auth.Ticket);

                        if (match < 0) break;

                        long anchor = regionBase + offset + match;

                        offset += match + 1;
                        if (UsedAnchors.ContainsKey((pid, started, anchor))) continue;
                        // AuthAP UIN may remain stale when the client changes character or establishes a second connection.
                        // The Python baseline locates candidates by ticket length and AuthType only,
                        // then independently cross-checks the key/mode through handler->connection.
                        if (Read32(handle, anchor - 4) != auth.Ticket.Length || Read32(handle, anchor + 0x404) != auth.AuthType) continue;

                        uint? mode = Read32(handle, anchor - 0x1d0);
                        byte[]? key = Read(handle, anchor - 0x1b8, 16);

                        if (mode is not (2 or 3 or 4) || key is null || key.All(x => x == 0)) continue;

                        uint? handler = Read32(handle, anchor - 0x1ec);
                        uint? connection = handler is > 0 ? Read32(handle, handler.Value + 0x88L) : null;
                        bool verified = connection is > 0 && Read32(handle, connection.Value + 0x84L) == mode
                            && Read(handle, connection.Value + 0x50L, 16) is { } other && key.SequenceEqual(other);

                        found.Add(new(pid, started, anchor, key, (int)mode.Value, verified));
                    }
                }
            }

            long next = regionBase + Math.Max(regionSize, 4096);

            if (next <= address) break;
            address = next;
        }
        return found;
    }

    private static byte[]? Read(SafeProcessHandle handle, long address, int count)
    {
        if (address <= 0 || count <= 0) return null;

        byte[] result = new byte[count];

        return ReadProcessMemory(handle, (nint)address, result, (nuint)count, out nuint actual) && actual == (nuint)count ? result : null;
    }

    private static uint? Read32(SafeProcessHandle handle, long address) => Read(handle, address, 4) is { } bytes ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
    private sealed record Candidate(int Process, long Started, long Anchor, byte[] Key, int Mode, bool Verified);

    private static SafeProcessHandle OpenReadableProcess(int processId, out int error)
    {
        // VirtualQueryEx requires PROCESS_QUERY_INFORMATION;
        // PROCESS_QUERY_LIMITED_INFORMATION alone cannot reliably scan AuthAP objects.
        // Matches the Python baseline's QUERY_INFORMATION | VM_READ permissions.
        Marshal.SetLastPInvokeError(0);

        var handle = OpenProcess(0x0400u | 0x0010u, false, processId);

        if (!handle.IsInvalid)
        {
            error = 0;
            return handle;
        }

        error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return new SafeProcessHandle(nint.Zero, ownsHandle: true);
    }

    private static bool TryEnableDebugPrivilege(out int error)
    {
        error = 0;
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x08, out nint token))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out Luid luid))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = 0x2 }
            };

            // AdjustTokenPrivileges may succeed while LastError reports ERROR_NOT_ALL_ASSIGNED.
            // Clear LastError first so an earlier API's stale error cannot affect this check.
            Marshal.SetLastPInvokeError(0);
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, nint.Zero, nint.Zero))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            error = Marshal.GetLastWin32Error();
            return error == 0;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryInformation info, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);
}
