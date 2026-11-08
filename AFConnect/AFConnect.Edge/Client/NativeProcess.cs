using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AFConnect.EDGE.Client;

internal static class NativeProcess
{
    private const uint Query = 0x0400, VmOperation = 0x0008, VmRead = 0x0010, VmWrite = 0x0020;
    internal readonly record struct ProcessRow(int Id, int ParentId);
    internal readonly record struct ImageMapping(ulong Base, string Path);

    internal sealed class Handle : SafeHandle
    {
        internal Handle(IntPtr value) : base(IntPtr.Zero, true) => SetHandle(value);
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    internal static Handle Open(int pid)
    {
        var value = OpenProcess(Query | VmOperation | VmRead | VmWrite, false, (uint)pid);

        if (value == IntPtr.Zero) throw Error($"OpenProcess({pid}); try starting EDGE with the same privileges as the game");
        return new Handle(value);
    }

    internal static List<ProcessRow> Processes(string fileName)
    {
        using var snapshot = new Handle(CreateToolhelp32Snapshot(2, 0));

        if (snapshot.IsInvalid) throw Error("CreateToolhelp32Snapshot(processes)");

        var row = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var result = new List<ProcessRow>();

        if (!Process32FirstW(snapshot, ref row))
        {
            if (Marshal.GetLastWin32Error() != 18) throw Error("Process32First");
            return result;
        }
        do
        {
            if (string.Equals(row.ImageName, fileName, StringComparison.OrdinalIgnoreCase))
                result.Add(new ProcessRow((int)row.ProcessId, (int)row.ParentProcessId));
        } while (Process32NextW(snapshot, ref row));
        if (Marshal.GetLastWin32Error() != 18) throw Error("Process32Next");
        return result;
    }

    internal static bool IsImage(int pid, string expected)
    {
        using var process = new Handle(OpenProcess(0x1000, false, (uint)pid));

        if (process.IsInvalid) return false;

        var name = new StringBuilder(32768);
        uint size = (uint)name.Capacity;

        return QueryFullProcessImageNameW(process, 0, name, ref size) &&
            string.Equals(Path.GetFullPath(name.ToString()), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAlive(Handle process) => GetExitCodeProcess(process, out var code) && code == 259;

    internal static ImageMapping? FindImage(Handle process, string fileName)
    {
        ulong address = 0;
        var seen = new HashSet<ulong>();

        while (address < 0x100000000UL)
        {
            var got = VirtualQueryEx(process, (IntPtr)checked((long)address), out var info, (nuint)Marshal.SizeOf<MemoryInfo>());

            if (got == 0) break;

            var allocation = (ulong)info.AllocationBase.ToInt64();

            if (info.Type == 0x1000000 && allocation != 0 && seen.Add(allocation))
            {
                var path = new StringBuilder(32768);

                if (GetMappedFileNameW(process, info.AllocationBase, path, (uint)path.Capacity) != 0 &&
                    path.ToString().EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase))
                    return new ImageMapping(allocation, ToDosPath(path.ToString()));
            }

            var next = checked((ulong)info.BaseAddress.ToInt64() + (ulong)info.RegionSize);

            if (next <= address) break;
            address = next;
        }
        return null;
    }

    private static string ToDosPath(string devicePath)
    {
        if (!devicePath.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)) return devicePath;
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            var target = new StringBuilder(32768);

            if (QueryDosDeviceW(drive, target, (uint)target.Capacity) == 0) continue;

            var prefix = target.ToString().TrimEnd('\\');

            if (devicePath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                return drive + devicePath[prefix.Length..];
        }
        throw new InvalidOperationException($"Cannot resolve the loaded image device path: {devicePath}");
    }

    internal static byte[] Read(Handle process, ulong address, int count)
    {
        var data = new byte[count];

        if (!ReadProcessMemory(process, Address(address), data, (nuint)count, out var read) || read != (nuint)count)
            throw Error($"ReadProcessMemory(0x{address:X})");
        return data;
    }

    /// <summary>
    /// Requires the entire existing trampoline to lie within committed, executable, non-guard pages.
    /// Readable bytes alone do not justify resuming the thread.
    /// </summary>
    internal static bool IsExecutableMemory(Handle process, ulong address, int count)
    {
        if (count <= 0 || address >= 0x1_0000_0000UL || (ulong)count > 0x1_0000_0000UL - address) return false;

        ulong end = address + (ulong)count;

        while (address < end)
        {
            if (VirtualQueryEx(process, Address(address), out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0 ||
                info.State != 0x1000 || (info.Protect & 0x100) != 0 ||
                (info.Protect & 0xFF) is not (0x10 or 0x20 or 0x40 or 0x80)) return false;

            ulong next = checked((ulong)info.BaseAddress.ToInt64() + (ulong)info.RegionSize);

            if (next <= address) return false;

            address = next;
        }

        return true;
    }

    internal static void RequireBytes(Handle process, ulong address, byte[] expected, string label)
    {
        var actual = Read(process, address, expected.Length);

        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"{label} signature mismatch at 0x{address:X}; expected {Convert.ToHexString(expected)}, found {Convert.ToHexString(actual)}. No patch was applied.");
    }

    internal static void WriteGuarded(Handle process, ulong address, byte[] expected, byte[] replacement, string label, Action? beforeWrite = null)
    {
        if (expected.Length != replacement.Length) throw new ArgumentException("Patch lengths differ.");
        RequireBytes(process, address, expected, label);
        WriteExecutable(process, address, replacement, beforeWrite);
    }

    internal static void WriteExecutable(Handle process, ulong address, byte[] data, Action? beforeWrite = null)
    {
        var pointer = Address(address);

        if (!VirtualProtectEx(process, pointer, (nuint)data.Length, 0x40, out var old))
            throw Error("VirtualProtectEx(write)");
        try
        {
            beforeWrite?.Invoke();
            if (!WriteProcessMemory(process, pointer, data, (nuint)data.Length, out var written) || written != (nuint)data.Length)
                throw Error($"WriteProcessMemory(0x{address:X})");
            if (!FlushInstructionCache(process, pointer, (nuint)data.Length)) throw Error("FlushInstructionCache");
            if (!Read(process, address, data.Length).AsSpan().SequenceEqual(data))
                throw new InvalidOperationException($"Memory patch verification failed at 0x{address:X}.");
        }
        finally
        {
            if (!VirtualProtectEx(process, pointer, (nuint)data.Length, old, out _))
                throw Error("VirtualProtectEx(restore)");
        }
    }

    internal static ulong Allocate(Handle process, int size)
    {
        var pointer = VirtualAllocEx(process, IntPtr.Zero, (nuint)Math.Max(size, 4096), 0x3000, 0x20);

        if (pointer == IntPtr.Zero) throw Error("VirtualAllocEx");

        var address = checked((ulong)pointer.ToInt64());

        if (address > uint.MaxValue)
        {
            Free(process, address);
            throw new InvalidOperationException("The compatibility trampoline must be within the x86 address space.");
        }
        return address;
    }

    internal static void Free(Handle process, ulong address)
    {
        if (!VirtualFreeEx(process, Address(address), 0, 0x8000)) throw Error("VirtualFreeEx");
    }

    internal static Handle OpenPrimaryThread(int pid)
    {
        using var snapshot = new Handle(CreateToolhelp32Snapshot(4, 0));

        if (snapshot.IsInvalid) throw Error("CreateToolhelp32Snapshot(threads)");

        var row = new ThreadEntry { Size = (uint)Marshal.SizeOf<ThreadEntry>() };
        Handle? primary = null;
        ulong oldest = ulong.MaxValue;

        if (Thread32First(snapshot, ref row))
        {
            do
            {
                if (row.OwnerProcessId != pid) continue;

                var thread = new Handle(OpenThread(0x40 | 0x2, false, row.ThreadId));

                if (thread.IsInvalid) { thread.Dispose(); continue; }
                if (GetThreadTimes(thread, out var creation, out _, out _, out _) && creation < oldest)
                {
                    primary?.Dispose();
                    primary = thread;
                    oldest = creation;
                }
                else thread.Dispose();
            } while (Thread32Next(snapshot, ref row));
        }
        return primary ?? throw new InvalidOperationException($"Cannot identify the primary thread of TGame PID={pid}.");
    }

    internal static void RequireSuspended(Handle thread)
    {
        // Probe the previous count, then immediately undo only this temporary suspension.
        var previous = SuspendThread(thread);

        if (previous == uint.MaxValue) throw Error("SuspendThread");
        if (ResumeThread(thread) == uint.MaxValue) throw Error("ResumeThread(probe restore)");
        if (previous != 1)
            throw new InvalidOperationException($"Expected TGame primary thread suspend count 1, got {previous}; refusing to patch an unverified launch.");
    }

    internal static void ResumePrimaryThread(Handle thread)
    {
        var previous = ResumeThread(thread);

        if (previous == uint.MaxValue) throw Error("ResumeThread");
        if (previous != 1) throw new InvalidOperationException($"TGame primary thread had unexpected suspend count {previous} at resume.");
    }

    private static IntPtr Address(ulong value) => new(checked((long)value));
    private static Win32Exception Error(string action) => new(Marshal.GetLastWin32Error(), action);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ImageName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry { public uint Size, Usage, ThreadId, OwnerProcessId; public int BasePriority, DeltaPriority; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(Handle snapshot, ref ProcessEntry row);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(Handle snapshot, ref ProcessEntry row);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(Handle snapshot, ref ThreadEntry row);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(Handle snapshot, ref ThreadEntry row);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(Handle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(Handle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(Handle process, IntPtr address, out MemoryInfo info, nuint size);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetMappedFileNameW(Handle process, IntPtr address, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDeviceW(string drive, StringBuilder target, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(Handle process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(Handle process, IntPtr address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(Handle process, IntPtr address, nuint size, uint protect, out uint oldProtect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(Handle process, IntPtr address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(Handle process, IntPtr address, nuint size, uint allocation, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(Handle process, IntPtr address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadTimes(Handle thread, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(Handle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(Handle thread);
}
