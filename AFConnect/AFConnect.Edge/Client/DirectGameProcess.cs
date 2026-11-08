using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AFConnect.EDGE.Client;

/// <summary>
/// Owns the newly created TGame process and its shared memory. Reclaim failed processes that were never
/// resumed; after success, keep the proxy alive until the game exits.
/// </summary>
public sealed class DirectGameProcess : IDisposable
{
    private readonly Process process;
    private readonly LauncherSharedMemory memory;

    private DirectGameProcess(Process process, LauncherSharedMemory memory)
    {
        this.process = process;
        this.memory = memory;
    }

    /// <summary>The exact process ID created by this launcher instance.</summary>
    public int Id => process.Id;

    /// <summary>Whether this game process has exited.</summary>
    public bool HasExited => process.HasExited;

    /// <summary>The game's exit code, available only after the process exits.</summary>
    public int ExitCode => process.ExitCode;

    /// <summary>
    /// Creates the process suspended and resumes its main thread only after validated patches and the
    /// shared-memory handoff. Leaves the executable on disk unchanged.
    /// </summary>
    public static DirectGameProcess Start(string root, LauncherIdentity identity, LauncherServer server, Action<string> log)
    {
        string validatedRoot = ClientCompatibility.ValidateGameRoot(root);
        string executable = Path.Combine(validatedRoot, "Binaries/Win32/TGame.exe");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, Show = 1 };
        var command = new StringBuilder($"\"{executable}\" -q {identity.Uin}");

        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 4, IntPtr.Zero,
            Path.GetDirectoryName(executable)!, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建玩家客户端。");

        using var processHandle = new NativeProcess.Handle(info.Process);
        using var thread = new NativeProcess.Handle(info.Thread);
        LauncherSharedMemory? memory = null;
        Process? process = null;

        try
        {
            process = Process.GetProcessById(checked((int)info.ProcessId));
            _ = process.Handle;

            var image = NativeProcess.FindImage(processHandle, "TGame.exe")
                ?? throw new InvalidOperationException("挂起的玩家映像尚未映射。");

            if (!NativeProcess.IsImage(process.Id, executable)) throw new InvalidOperationException("新建进程映像路径不匹配。");

            NativeProcess.RequireSuspended(thread);
            ClientCompatibility.PatchDateTime(processHandle, image.Base);
            memory = new LauncherSharedMemory(process.Id, identity, server);
            NativeProcess.ResumePrimaryThread(thread);
            log($"[启动] 已直接启动 TGame.exe，PID={process.Id}，UIN={identity.Uin}；共享内存及日期兼容补丁已完成。");
            return new DirectGameProcess(process, memory);
        }
        catch
        {
            // Reclaim only the process handle returned by this CreateProcessW call; never terminate the user's other processes by application name.
            TerminateProcess(info.Process, 1);
            WaitForSingleObject(info.Process, 5000);
            memory?.Dispose();
            process?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits for this game to exit. Cancellation stops the wait without terminating the running game.
    /// </summary>
    public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);

    /// <summary>
    /// Releases handoff memory and process-observation handles. Call only after the game exits so login
    /// information remains available for its full lifetime.
    /// </summary>
    public void Dispose()
    {
        memory.Dispose();
        process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal IntPtr ReservedData, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        string directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr process, uint milliseconds);
}
