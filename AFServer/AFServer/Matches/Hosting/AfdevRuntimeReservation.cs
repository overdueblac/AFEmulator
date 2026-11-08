using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Reserves physical identity, runtime slot, and ports across hosts. Failed cleanup retains ownership;
/// candidate ports must not be mistaken for reservations.
/// </summary>
/// <remarks>
/// Named objects provide handle-lifetime exclusivity without thread-owned Mutex across await. The public
/// UDP port remains exclusively bound for the entire reservation.
/// </remarks>
public sealed class AfdevRuntimeReservation
{
    private readonly object gate = new();
    private readonly List<Mutex> leases = [];
    private Socket? publicSocket;
    private Socket? targetSocket;
    private AfdevOwnedProcess? targetProcess;
    private SafeFileHandle? reservedDirectory;
    private FileStream? reservedExecutable;
    private bool released;

    private AfdevRuntimeReservation(Guid instanceId, int slot, int publicPort, int targetPort)
    {
        InstanceId = instanceId;
        Slot = slot;
        PublicPort = publicPort;
        TargetPort = targetPort;
    }

    /// <summary>The physical-instance identity exclusive to this reservation.</summary>
    public Guid InstanceId { get; }

    /// <summary>The runtime slot reserved across hosts.</summary>
    public int Slot { get; }

    /// <summary>
    /// Candidate UDP port kept exclusively bound by C# as a reservation only. Player data uses CONNECT
    /// frames; no raw UDP forwarding is exposed.
    /// </summary>
    public int PublicPort { get; }

    /// <summary>UDP port first reserved by C#, then handed to the owned AFDEV process.</summary>
    public int TargetPort { get; }

    /// <summary>True after port release has been rechecked and all named reservations closed.</summary>
    public bool IsReleased { get { lock (gate) return released; } }

    /// <summary>
    /// Atomically acquires cross-host physical-instance, slot, and both port reservations, binding both UDP
    /// ports exclusively.
    /// </summary>
    public static AfdevRuntimeReservation Acquire(MatchRuntimeOptions options, Guid instanceId, int slot)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AFDEV 运行预约只支持 Windows。");
        if (instanceId == Guid.Empty || slot < 0 || slot >= options.MaxInstances)
            throw new ArgumentException("运行预约需要非空 物理实例 和配置范围内的槽。");

        var reservation = new AfdevRuntimeReservation(instanceId, slot, options.PublicPortBase + slot, options.TargetPortBase + slot);

        try
        {
            if (options.UsesReservedRuntime)
                reservation.AcquireReservedRuntime(options.GetRuntimeRoot(slot), options.GetRuntimeExecutable(slot));

            reservation.AcquireLease("instance-" + instanceId.ToString("N"));
            reservation.AcquireLease("slot-" + slot);
            reservation.AcquireLease("udp-" + reservation.PublicPort);
            reservation.AcquireLease("udp-" + reservation.TargetPort);
            reservation.publicSocket = BindExclusive(reservation.PublicPort);
            reservation.targetSocket = BindExclusive(reservation.TargetPort);

            return reservation;
        }
        catch
        {
            reservation.targetSocket?.Dispose();
            reservation.publicSocket?.Dispose();
            reservation.CloseLeases();

            throw;
        }
    }

    /// <summary>
    /// Hands the upstream port to this host's exact owned process before the first native operation that
    /// could bind a socket.
    /// </summary>
    /// <remarks>
    /// A named lease coordinates product hosts during handoff. VerifyTargetOwner explicitly rejects an
    /// unrelated process taking the port.
    /// </remarks>
    public void ReleaseTargetForNative(AfdevOwnedProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (gate)
        {
            ThrowIfReleased();

            if (!process.IsAlive || targetProcess != null || targetSocket == null)
                throw new InvalidOperationException("上游端口仅能从独占预留状态交给一次仍存活的原生启动。");

            targetProcess = process;
            targetSocket.Dispose();
            targetSocket = null;
        }
    }

    /// <summary>
    /// Verifies that the target Windows UDP port belongs only to this exact root process. Returns false
    /// before binding and fails directly on takeover.
    /// </summary>
    public bool VerifyTargetOwner(AfdevOwnedProcess process, bool requireLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (gate)
        {
            ThrowIfReleased();

            if (!ReferenceEquals(targetProcess, process) || targetSocket != null)
                throw new InvalidOperationException("目标 socket 归属核实必须绑定当前端口交接的精确进程对象。");
            if (!process.IsAlive)
                throw new InvalidOperationException("AFDEV 根进程已经退出，不能据其 PID 接受 socket 证据。");

            var owners = ReadUdpOwners(TargetPort, requireLoopback);

            if (owners.Count == 0)
                return false;
            if (owners.Any(owner => owner != process.ProcessId))
                throw new IOException($"AFDEV 上游 UDP {TargetPort} 被其它进程占用，拒绝接受就绪状态。");
            if (!process.IsAlive)
                throw new InvalidOperationException("核对 socket 归属期间 AFDEV 根进程已经退出。");

            return true;
        }
    }

    /// <summary>
    /// Rebinds the upstream port exclusively only after the exact process object confirms whole-Job exit,
    /// preserving other reservations for retry.
    /// </summary>
    /// <remarks>
    /// Bind failure retains process association, named reservations, and the public socket for retry after
    /// external port use ends.
    /// </remarks>
    public void ReclaimTargetAfterProcessStopped()
    {
        lock (gate)
        {
            ThrowIfReleased();
            ReclaimTarget();
        }
    }

    /// <summary>
    /// Releases all reservations after confirming previous-tree exit and exclusive upstream rebind. Failure
    /// retains ownership for another call.
    /// </summary>
    public void ReleaseAfterProcessStopped()
    {
        lock (gate)
        {
            if (released)
                return;

            ReclaimTarget();
            targetSocket!.Dispose();
            publicSocket!.Dispose();
            targetSocket = null;
            publicSocket = null;
            CloseLeases();
            released = true;
        }
    }

    private void ReclaimTarget()
    {
        if (targetProcess != null && !targetProcess.IsStopped)
            throw new InvalidOperationException("旧 AFDEV 进程树和句柄尚未确认回收，禁止释放或复用运行预约。");
        if (targetSocket == null)
            targetSocket = BindExclusive(TargetPort);

        targetProcess = null;
    }

    private void AcquireLease(string name)
    {
        // Global names are unique across Windows sessions. Check creation results only; never use WaitOne/ReleaseMutex.
        var lease = new Mutex(false, "Global\\AFServer-Afdev-" + name, out var createdNew);

        if (!createdNew)
        {
            lease.Dispose();

            throw new IOException($"AFDEV 运行占用已存在：{name}");
        }

        leases.Add(lease);
    }

    private void AcquireReservedRuntime(string root, string executable)
    {
        var fullRoot = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, root);

        RuntimePath.RejectReparsePoints(fullRoot);

        // FILE_LIST_DIRECTORY makes shared-access checks effective. Zero-access attribute-only handles cannot prevent directory renaming.
        // Exclude DELETE sharing to prevent moving/removing the actual client directory before whole-tree exit. The handle grants no file-write rights.
        reservedDirectory = OpenReservedDirectory(fullRoot, 1, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

        if (reservedDirectory.IsInvalid)
            throw new IOException("无法保留 AFDEV 共用客户端目录。", new Win32Exception(Marshal.GetLastWin32Error()));

        reservedExecutable = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);

        var identity = AfdevInstanceWorkspace.ReadFileSystemIdentity(executable);
        var pathKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())));
        var identityKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

        // One resource root supports concurrent readers. Reserve by executable path and file identity to prevent aliases bypassing entry-point exclusivity.
        AcquireLease("reserved-executable-path-" + pathKey);
        AcquireLease("reserved-executable-file-" + identityKey);
    }

    private void CloseLeases()
    {
        for (var index = leases.Count - 1; index >= 0; index--)
            leases[index].Dispose();

        leases.Clear();
        reservedExecutable?.Dispose();
        reservedExecutable = null;
        reservedDirectory?.Dispose();
        reservedDirectory = null;
    }

    private void ThrowIfReleased()
    {
        if (released)
            throw new ObjectDisposedException(nameof(AfdevRuntimeReservation));
    }

    private static Socket BindExclusive(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };

        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            return socket;
        }
        catch
        {
            socket.Dispose();

            throw;
        }
    }

    private static List<uint> ReadUdpOwners(int port, bool requireLoopback)
    {
        uint length = 0;
        var result = GetExtendedUdpTable(IntPtr.Zero, ref length, false, 2, 1, 0);

        if (result != 0 && result != 122)
            throw new Win32Exception((int)result, "GetExtendedUdpTable(size)");

        // The Windows table may change during querying. Retry with its new length; reject truncated or unverified results.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)Math.Max(length, 4)));

            try
            {
                result = GetExtendedUdpTable(buffer, ref length, false, 2, 1, 0);

                if (result == 122)
                    continue;
                if (result != 0)
                    throw new Win32Exception((int)result, "GetExtendedUdpTable(OWNER_PID)");

                var count = checked((uint)Marshal.ReadInt32(buffer));

                if (4UL + 12UL * count > length)
                    throw new InvalidDataException("Windows UDP 所有权表长度无效。");

                var owners = new List<uint>();

                for (var index = 0U; index < count; index++)
                {
                    var offset = checked(4 + (int)index * 12);
                    var encodedPort = unchecked((uint)Marshal.ReadInt32(buffer, offset + 4));
                    var owner = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                    var localPort = (int)(((encodedPort & 0xff) << 8) | ((encodedPort >> 8) & 0xff));

                    if (localPort == port)
                    {
                        if (requireLoopback && unchecked((uint)Marshal.ReadInt32(buffer, offset)) != 0x0100007F)
                            throw new IOException($"AFDEV 上游 UDP {port} 的 Windows 绑定地址不是 127.0.0.1。");

                        owners.Add(owner);
                    }
                }

                return owners;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        throw new IOException("Windows UDP 所有权表持续变化，无法取得一致的端口证据。");
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, uint tableClass, uint reserved);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenReservedDirectory(string path, uint access, uint sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);
}
