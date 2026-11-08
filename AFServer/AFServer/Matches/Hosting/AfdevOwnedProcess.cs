using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Owns the exact AFDEV process created suspended by this host, its main thread, and a kill-on-close Job.
/// Accepts no external PID.
/// </summary>
/// <remarks>
/// Release handles only after StopAsync confirms root-process and whole-Job exit. Retain this object on
/// cleanup failure for retry.
/// </remarks>
public sealed partial class AfdevOwnedProcess : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim stopGate = new(1, 1);
    private readonly List<OwnedKernelHandle> remoteThreads = [];
    private OwnedKernelHandle? process;
    private OwnedKernelHandle? mainThread;
    private OwnedKernelHandle? job;
    private bool assignedToJob;
    private bool stopped;

    private AfdevOwnedProcess() { }

    /// <summary>
    /// Root process ID returned by CreateProcessW, used only for auditing and socket-ownership
    /// cross-checks.
    /// </summary>
    public uint ProcessId { get; private set; }

    /// <summary>Main thread ID returned by CreateProcessW.</summary>
    public uint MainThreadId { get; private set; }

    /// <summary>Windows creation time read from the retained root-process handle.</summary>
    public DateTimeOffset CreationTimeUtc { get; private set; }

    /// <summary>
    /// Indicates only that the retained root process has not exited; it is not native-readiness evidence.
    /// </summary>
    public bool IsAlive
    {
        get
        {
            lock (gate)
                return !stopped && process != null && WaitForSingleObject(process, 0) == WaitTimeout;
        }
    }

    /// <summary>True only after confirming whole-tree exit and closing every process handle.</summary>
    public bool IsStopped { get { lock (gate) return stopped; } }

    /// <summary>
    /// Creates x86 AFDEV suspended with the validated client's Binaries/Win32 as its working directory,
    /// then assigns it to the Job before returning.
    /// </summary>
    /// <remarks>
    /// The caller installs verified patches before resuming the main thread. A post-creation cleanup
    /// exception retains the process object; keep it and retry cleanup.
    /// </remarks>
    public static AfdevOwnedProcess CreateSuspended(AfdevInstanceWorkspace workspace, string arguments)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AFDEV 原生宿主只支持 Windows。");

        var expectedWorkingDirectory = Path.GetFullPath(Path.Combine(workspace.RuntimeRoot, "Binaries", "Win32"));
        var expectedExecutable = Path.GetFullPath(workspace.ExecutablePath);

        if (!Path.IsPathFullyQualified(workspace.Directory) || !Path.IsPathFullyQualified(workspace.RuntimeRoot) ||
            (!workspace.UsesReservedRuntime && !string.Equals(workspace.Directory, workspace.RuntimeRoot, StringComparison.OrdinalIgnoreCase)) ||
            (workspace.UsesReservedRuntime && (RuntimePath.IsWithinRoot(workspace.RuntimeRoot, workspace.Directory) ||
                RuntimePath.IsWithinRoot(workspace.Directory, workspace.RuntimeRoot))) ||
            !string.Equals(Path.GetFullPath(workspace.WorkingDirectory), expectedWorkingDirectory, StringComparison.OrdinalIgnoreCase) ||
            !RuntimePath.IsAfdevExecutablePath(Path.GetRelativePath(workspace.RuntimeRoot, expectedExecutable)) ||
            arguments.Contains('\0'))
            throw new InvalidDataException("AFDEV 进程只能从已核验客户端 Binaries/Win32 和固定 EXE 路径启动，原位资源与诊断目录必须隔离。");

        RuntimePath.RejectReparsePoints(expectedWorkingDirectory);
        RuntimePath.RejectReparsePoints(expectedExecutable);
        VerifyX86Executable(expectedExecutable);

        var owned = new AfdevOwnedProcess();

        try
        {
            owned.job = CreateJobObjectW(IntPtr.Zero, null);
            ThrowIfInvalid(owned.job, "CreateJobObjectW");

            var limits = new JobExtendedLimitInformation
            {
                BasicLimitInformation = new JobBasicLimitInformation { LimitFlags = JobKillOnClose }
            };

            if (!SetInformationJobObject(owned.job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
                throw NativeFailure("SetInformationJobObject(KILL_ON_JOB_CLOSE)");

            // CREATE_NO_WINDOW affects only console windows; also request an initially hidden GUI window.
            // The native engine may later change visibility. Verify actual windows rather than infer behavior solely from creation flags.
            var startup = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfo>(),
                Flags = 0x00000001, // STARTF_USESHOWWINDOW
                ShowWindow = 0 // SW_HIDE
            };
            var commandLine = new StringBuilder('"' + expectedExecutable + '"' + (arguments.Length == 0 ? "" : " " + arguments));

            if (!CreateProcessW(expectedExecutable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspendedFlag | CreateNoWindow, IntPtr.Zero, expectedWorkingDirectory, ref startup, out var information))
                throw NativeFailure("CreateProcessW(CREATE_SUSPENDED)");

            owned.process = new OwnedKernelHandle(information.Process);
            owned.mainThread = new OwnedKernelHandle(information.Thread);
            owned.ProcessId = information.ProcessId;
            owned.MainThreadId = information.ThreadId;

            if (!AssignProcessToJobObject(owned.job, owned.process))
                throw NativeFailure("AssignProcessToJobObject");

            owned.assignedToJob = true;

            if (!GetProcessTimes(owned.process, out var creation, out _, out _, out _))
                throw NativeFailure("GetProcessTimes");

            owned.CreationTimeUtc = DateTimeOffset.FromFileTime(creation).ToUniversalTime();

            if (!IsWow64Process2(owned.process, out var processMachine, out var nativeMachine) ||
                (processMachine != 0x014c && !(processMachine == 0 && nativeMachine == 0x014c)))
                throw new InvalidDataException("挂起创建的 AFDEV 不是已支持的 x86 进程。");

            return owned;
        }
        catch (Exception creationFailure)
        {
            try { owned.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            catch (Exception cleanupFailure)
            {
                throw new AfdevProcessCreationException(owned, creationFailure, cleanupFailure);
            }

            throw;
        }
    }

    /// <summary>
    /// Reads exact target-memory ranges through the owned handle. Short reads or process exit fail.
    /// </summary>
    public byte[] ReadMemory(uint address, int length)
    {
        if (length < 0 || (ulong)address + (uint)length > (ulong)uint.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(length));

        var buffer = new byte[length];

        lock (gate)
        {
            var handle = RequireLiveProcess();

            if (length != 0 && (!ReadProcessMemory(handle, new IntPtr((long)address), buffer, (nuint)length, out var read) || read != (nuint)length))
                throw NativeFailure($"ReadProcessMemory(0x{address:X8}, {length})");
        }

        return buffer;
    }

    /// <summary>
    /// Writes an exact memory range while preserving original protection and flushing the target
    /// instruction cache.
    /// </summary>
    public void WriteMemory(uint address, ReadOnlySpan<byte> data)
    {
        if ((ulong)address + (uint)data.Length > (ulong)uint.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(data));
        if (data.IsEmpty)
            return;

        var buffer = data.ToArray();
        var pointer = new IntPtr((long)address);

        lock (gate)
        {
            var handle = RequireLiveProcess();

            VerifySingleProtectionRegion(handle, address, buffer.Length);

            if (!VirtualProtectEx(handle, pointer, (nuint)buffer.Length, PageExecuteReadWrite, out var previousProtection))
                throw NativeFailure($"VirtualProtectEx(0x{address:X8})");

            Exception? writeFailure = null;

            try
            {
                if (!WriteProcessMemory(handle, pointer, buffer, (nuint)buffer.Length, out var written) || written != (nuint)buffer.Length)
                    throw NativeFailure($"WriteProcessMemory(0x{address:X8}, {buffer.Length})");
                if (!FlushInstructionCache(handle, pointer, (nuint)buffer.Length))
                    throw NativeFailure("FlushInstructionCache");
            }
            catch (Exception failure) { writeFailure = failure; }

            if (!VirtualProtectEx(handle, pointer, (nuint)buffer.Length, previousProtection, out _))
            {
                var restoreFailure = NativeFailure($"VirtualProtectEx restore(0x{address:X8})");

                throw writeFailure == null ? restoreFailure : new AggregateException(writeFailure, restoreFailure);
            }

            if (writeFailure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writeFailure).Throw();
        }
    }

    /// <summary>
    /// Allocates memory in the owned x86 process for its lifetime; never reclaims executing native-call
    /// code midway.
    /// </summary>
    public uint AllocateMemory(int size, bool executable = true)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        lock (gate)
        {
            var handle = RequireLiveProcess();
            var pointer = VirtualAllocEx(handle, IntPtr.Zero, (nuint)size, MemCommit | MemReserve,
                executable ? PageExecuteReadWrite : PageReadWrite);
            var address = unchecked((ulong)pointer.ToInt64());

            if (pointer == IntPtr.Zero)
                throw NativeFailure("VirtualAllocEx");
            if (address > uint.MaxValue || address + (uint)size > (ulong)uint.MaxValue + 1)
                throw new InvalidDataException("AFDEV 远程分配地址超出 x86 地址空间。");

            return (uint)address;
        }
    }

    /// <summary>
    /// Reads the complete x86 CONTEXT, preserving floating-point, XMM, and debug registers. The main thread
    /// must already be suspended.
    /// </summary>
    public AfdevX86ThreadContext GetMainThreadContext()
    {
        lock (gate)
        {
            RequireLiveProcess();

            var context = new AfdevX86ThreadContext();
            var result = Environment.Is64BitProcess
                ? Wow64GetThreadContext(mainThread!, context.Bytes)
                : GetThreadContext(mainThread!, context.Bytes);

            if (!result)
                throw NativeFailure("GetThreadContext(x86)");

            return context;
        }
    }

    /// <summary>
    /// Writes the complete x86 CONTEXT. The caller changes only explicitly owned registers, with the main
    /// thread suspended.
    /// </summary>
    public void SetMainThreadContext(AfdevX86ThreadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (gate)
        {
            RequireLiveProcess();

            var result = Environment.Is64BitProcess
                ? Wow64SetThreadContext(mainThread!, context.Bytes)
                : SetThreadContext(mainThread!, context.Bytes);

            if (!result)
                throw NativeFailure("SetThreadContext(x86)");
        }
    }

    /// <summary>
    /// Increments the main-thread suspend count and returns its previous value. Pair with ResumeMainThread.
    /// </summary>
    public uint SuspendMainThread()
    {
        lock (gate)
        {
            RequireLiveProcess();

            var previous = Environment.Is64BitProcess ? Wow64SuspendThread(mainThread!) : SuspendThread(mainThread!);

            if (previous == uint.MaxValue)
                throw NativeFailure("SuspendThread(x86)");

            return previous;
        }
    }

    /// <summary>
    /// Decrements the main-thread suspend count. The process belongs to a kill-on-close Job before its
    /// first resume.
    /// </summary>
    public uint ResumeMainThread()
    {
        lock (gate)
        {
            RequireLiveProcess();

            if (!assignedToJob)
                throw new InvalidOperationException("未纳入 Job 的 AFDEV 不允许恢复执行。");

            var previous = ResumeThread(mainThread!);

            if (previous == uint.MaxValue)
                throw NativeFailure("ResumeThread");

            return previous;
        }
    }

    /// <summary>
    /// Executes a validated remote entry point. Timeout or cancellation retains thread handles for
    /// subsequent whole-process StopAsync cleanup.
    /// </summary>
    public async Task<uint> CreateRemoteThreadAsync(uint entryPoint, uint parameter, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        OwnedKernelHandle thread;

        lock (gate)
        {
            var handle = RequireLiveProcess();

            thread = CreateRemoteThread(handle, IntPtr.Zero, 0, new IntPtr((long)entryPoint), new IntPtr((long)parameter), 0, out _);
            ThrowIfInvalid(thread, "CreateRemoteThread");
            remoteThreads.Add(thread);
        }

        var timer = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (gate)
            {
                if (stopped)
                    throw new InvalidOperationException("AFDEV 已停止，远程调用结果不可用。");

                var status = WaitForSingleObject(thread, 0);

                if (status == WaitObject0)
                {
                    if (!GetExitCodeThread(thread, out var exitCode))
                        throw NativeFailure("GetExitCodeThread");

                    remoteThreads.Remove(thread);
                    thread.Dispose();

                    return exitCode;
                }

                if (status != WaitTimeout)
                    throw NativeFailure("WaitForSingleObject(remote thread)");
            }

            if (timer.Elapsed >= timeout)
                throw new TimeoutException("AFDEV 远程调用未在期限内结束；其进程仍由宿主持有。");

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Terminates the entire owned Job and closes handles after confirming zero active processes and root
    /// exit. Failure or cancellation retains the object for retry.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (gate)
            {
                if (stopped)
                    return;

                if (process != null && !IsRootExited())
                {
                    // A creation failure before successful Job assignment still retains the original CreateProcessW handle. Never open another process by PID.
                    if (assignedToJob)
                    {
                        if (!TerminateJobObject(job!, 1))
                            throw NativeFailure("TerminateJobObject");
                    }
                    else if (!TerminateProcess(process, 1))
                        throw NativeFailure("TerminateProcess(owned creation failure)");
                }
                else if (assignedToJob && GetActiveProcessCount() != 0 && !TerminateJobObject(job!, 1))
                    throw NativeFailure("TerminateJobObject(descendants)");
            }

            var timer = Stopwatch.StartNew();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (gate)
                {
                    if (IsRootExited() && (!assignedToJob || GetActiveProcessCount() == 0))
                    {
                        foreach (var thread in remoteThreads)
                            thread.Dispose();

                        remoteThreads.Clear();
                        mainThread?.Dispose();
                        process?.Dispose();
                        job?.Dispose();
                        mainThread = null;
                        process = null;
                        job = null;
                        stopped = true;

                        return;
                    }
                }

                if (timer.Elapsed >= timeout)
                    throw new TimeoutException("AFDEV 进程树尚未完全退出；句柄和运行占用必须保留以便再次清理。");

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { stopGate.Release(); }
    }

    /// <summary>
    /// Attempts to reclaim the owned process tree within ten seconds. Throws on failure while retaining
    /// handles.
    /// </summary>
    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(10)));

    private OwnedKernelHandle RequireLiveProcess()
    {
        if (stopped || process == null || IsRootExited())
            throw new InvalidOperationException("本宿主拥有的 AFDEV 进程已经退出或尚未创建。");

        return process;
    }

    private bool IsRootExited()
    {
        if (process == null)
            return true;

        var status = WaitForSingleObject(process, 0);

        if (status != WaitObject0 && status != WaitTimeout)
            throw NativeFailure("WaitForSingleObject(owned process)");

        return status == WaitObject0;
    }

    private uint GetActiveProcessCount()
    {
        if (!QueryInformationJobObject(job!, 1, out var accounting, (uint)Marshal.SizeOf<JobBasicAccountingInformation>(), IntPtr.Zero))
            throw NativeFailure("QueryInformationJobObject(active processes)");

        return accounting.ActiveProcesses;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private static void VerifySingleProtectionRegion(OwnedKernelHandle handle, uint address, int length)
    {
        var information = new byte[Environment.Is64BitProcess ? 48 : 28];

        if (VirtualQueryEx(handle, new IntPtr((long)address), information, (nuint)information.Length) != (nuint)information.Length)
            throw NativeFailure($"VirtualQueryEx(0x{address:X8})");

        var regionStart = Environment.Is64BitProcess
            ? BinaryPrimitives.ReadUInt64LittleEndian(information)
            : BinaryPrimitives.ReadUInt32LittleEndian(information);
        var regionLength = Environment.Is64BitProcess
            ? BinaryPrimitives.ReadUInt64LittleEndian(information.AsSpan(24))
            : BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(12));

        // VirtualProtectEx returns only the first page's old protection. Writing across protection regions would restore later pages incorrectly, so callers must split ranges explicitly.
        if (address < regionStart || (ulong)address - regionStart + (uint)length > regionLength)
            throw new InvalidDataException("AFDEV 内存写入跨越不同保护区，必须分段校验和写入。");
    }

    private static void VerifyX86Executable(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(input);

        if (input.Length < 64 || reader.ReadUInt16() != 0x5a4d)
            throw new InvalidDataException("AFDEV 不是有效的 PE 文件。");

        input.Position = 0x3c;

        var peOffset = reader.ReadUInt32();

        if (peOffset > input.Length - 26)
            throw new InvalidDataException("AFDEV PE 头超出文件范围。");

        input.Position = peOffset;

        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x014c)
            throw new InvalidDataException("AFDEV 必须是 x86 PE。");

        input.Position = peOffset + 24;

        if (reader.ReadUInt16() != 0x010b)
            throw new InvalidDataException("AFDEV 必须使用 PE32 可选头。");
    }

    private static Win32Exception NativeFailure(string operation)
    {
        var error = Marshal.GetLastWin32Error();

        return new Win32Exception(error, $"{operation} Win32={error}: {new Win32Exception(error).Message}");
    }

    private static void ThrowIfInvalid(OwnedKernelHandle handle, string operation)
    {
        if (handle.IsInvalid)
            throw NativeFailure(operation);
    }
}

/// <summary>
/// Editable register view of a complete x86 CONTEXT. Preserves unchanged floating-point and XMM state
/// verbatim.
/// </summary>
public sealed class AfdevX86ThreadContext
{
    internal byte[] Bytes { get; } = new byte[716];

    internal AfdevX86ThreadContext() => BinaryPrimitives.WriteUInt32LittleEndian(Bytes, 0x0001003f);

    /// <summary>The x86 instruction pointer.</summary>
    public uint Eip { get => Read(184); set => Write(184, value); }

    /// <summary>The x86 stack pointer.</summary>
    public uint Esp { get => Read(196); set => Write(196, value); }

    /// <summary>The x86 EAX register.</summary>
    public uint Eax { get => Read(176); set => Write(176, value); }

    /// <summary>The x86 EBX register.</summary>
    public uint Ebx { get => Read(164); set => Write(164, value); }

    /// <summary>The x86 ECX register.</summary>
    public uint Ecx { get => Read(172); set => Write(172, value); }

    /// <summary>The x86 EDX register.</summary>
    public uint Edx { get => Read(168); set => Write(168, value); }

    /// <summary>The x86 EBP register.</summary>
    public uint Ebp { get => Read(180); set => Write(180, value); }

    private uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset, 4));
    private void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset, 4), value);
}

/// <summary>
/// Post-creation cleanup failure carrying the object that still owns process handles. The caller must
/// retain its runtime reservation.
/// </summary>
public sealed class AfdevProcessCreationException : AggregateException
{
    internal AfdevProcessCreationException(AfdevOwnedProcess ownedProcess, Exception creationFailure, Exception cleanupFailure)
        : base("AFDEV 创建失败且清理未完成，必须保留进程所有权并重试清理。", creationFailure, cleanupFailure)
        => OwnedProcess = ownedProcess;

    /// <summary>The owned object still requiring StopAsync.</summary>
    public AfdevOwnedProcess OwnedProcess { get; }
}
