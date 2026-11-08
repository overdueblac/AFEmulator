using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AFServer.AssaultFire.Matches;

public sealed partial record AfdevInstanceWorkspace
{
    private string? ownedDirectory;
    private string? ownedDirectoryIdentity;
    private bool resourcesRemoved;

    /// <summary>
    /// Checks the target volume for private resources plus reserved space. Shared packages count once;
    /// concurrent disk use can still cause I/O failure.
    /// </summary>
    internal static void EnsureCopySpace(MatchRuntimeOptions options, RuntimeManifest manifest)
    {
        var bytes = manifest.Files.Where(file => !AfdevSharedResources.IsShared(file))
            .Aggregate(0L, (total, file) => checked(total + file.Size));
        var required = checked(bytes + options.MinimumFreeSpaceBytes);
        var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.InstancesRoot))!).AvailableFreeSpace;

        if (available < required)
            throw new IOException($"AFDEV 准备前空间不足：需要 {required} 字节（私有资源 {bytes} + 保留 {options.MinimumFreeSpaceBytes}），可用 {available} 字节；共享静态包不重复占用数据空间。");
    }

    /// <summary>
    /// Called only after the host reclaims its Job, threads, handles, ports, and transient state. In-place
    /// resources release read-only leases; only legacy copies are deleted.
    /// </summary>
    /// <remarks>
    /// Memory ownership comes only from Prepare. Never scans historical directories or gains deletion
    /// rights from PID/STOPPED files. The same host retries failures.
    /// </remarks>
    internal void ArchiveAndRemoveStoppedResources()
    {
        if (resourcesRemoved) return;
        if (ownedDirectory == null || !Path.IsPathFullyQualified(Directory) ||
            !string.Equals(Directory, ownedDirectory, StringComparison.Ordinal))
            throw new IOException("AFDEV 资源回收缺少本次准备操作的目录所有权。");

        RuntimePath.RejectReparsePoints(Directory);

        if (!string.Equals(ReadDirectoryIdentity(Directory), ownedDirectoryIdentity, StringComparison.Ordinal))
            throw new IOException("AFDEV 自有目录的文件系统身份已改变；拒绝删除替换后的未知目录。");

        if (UsesReservedRuntime && (RuntimePath.IsWithinRoot(RuntimeRoot, Directory) || RuntimePath.IsWithinRoot(Directory, RuntimeRoot)))
            throw new IOException("AFDEV 原位客户端与自有诊断目录边界重叠，拒绝回收。");

        // In-place mode skips resource scanning entirely. Legacy mode scans first and deletes nothing if it finds links or replaced directories.
        var files = new List<string>();
        var directories = new List<string>();

        foreach (var name in UsesReservedRuntime ? [] : new[] { "Binaries", "Engine", "TGame" })
        {
            var root = RuntimePath.ResolveUnderRoot(Directory, name);

            if (File.Exists(root)) throw new IOException($"AFDEV 资源树被替换为文件：{root}");
            if (!System.IO.Directory.Exists(root)) continue;

            var pending = new Stack<string>();

            pending.Push(root);

            while (pending.TryPop(out var current))
            {
                RuntimePath.RejectReparsePoints(current);
                directories.Add(current);

                foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(current))
                {
                    RuntimePath.RejectReparsePoints(path);

                    if (System.IO.Directory.Exists(path)) pending.Push(path);
                    else files.Add(path);
                }
            }
        }

        // Lifecycle records already retain exact process identity and strict readiness/failure diagnostics. The original location is the evidence archive; do not copy complete resources to retain logs.
        // Do not collect arbitrary login URLs or credentials from native logs. Retain only controlled host-generated evidence.
        var evidence = new List<object>();

        foreach (var path in new[] { "allocation.json", "runtime-manifest.json", "logs/lifecycle.jsonl" })
        {
            var fullPath = RuntimePath.ResolveUnderRoot(Directory, path);
            using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (input.Length > 64L * 1024 * 1024)
                throw new IOException($"AFDEV 证据超过单文件 64 MiB 归档预算，保留资源等待人工核查：{path}");

            evidence.Add(new ArchiveAndRemoveStoppedResourcesJson1 { path = path, bytes = input.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(input)) });
        }

        WriteRetention("ARCHIVED_RESOURCES_PENDING", evidence, null);

        try
        {
            foreach (var file in files)
            {
                RuntimePath.RejectReparsePoints(file);
                File.Delete(file);
            }

            foreach (var directory in directories.AsEnumerable().Reverse())
            {
                RuntimePath.RejectReparsePoints(directory);
                System.IO.Directory.Delete(directory, recursive: false);
            }

            WriteRetention(UsesReservedRuntime ? "RESERVED_RUNTIME_RETAINED" : "RESOURCES_REMOVED", evidence, null);
            // Delete only this instance's link. Other instances retain their references; shared-file write protection ends only with the final reference.
            sharedResources?.Dispose();
            sharedResources = null;
            reservedResources?.Dispose();
            reservedResources = null;
            resourcesRemoved = true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Failure means disk resources still need cleanup; the process has stopped. Retain the original object and reservation to prevent unbounded retry copies.
            try { WriteRetention("RESOURCE_DELETE_PENDING_PROCESS_STOPPED", evidence, failure.Message); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            throw new IOException("AFDEV 自有进程已停止、证据已归档，但私有资源删除失败；保留同一宿主重试。", failure);
        }
    }

    private void WriteRetention(string state, List<object> evidence, string? error)
    {
        var destination = RuntimePath.ResolveUnderRoot(Directory, "retention.json");
        var temporary = RuntimePath.ResolveUnderRoot(Directory, ".retention.tmp");

        try
        {
            File.WriteAllText(temporary, global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(new WriteRetentionJson1
            {
                state = state, instance_id = InstanceId, allocation_id = AllocationId, stopped_resources_to_retain = 0,
                preserved_reserved_runtime = UsesReservedRuntime ? RuntimeRoot : null,
                observed_utc = DateTimeOffset.UtcNow, evidence = evidence, error = error
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static string ReadDirectoryIdentity(string path)
        => ReadFileSystemIdentity(path);

    /// <summary>
    /// Reads volume and file indices for ownership checks and shared-executable leases, eliminating path
    /// aliases.
    /// </summary>
    internal static string ReadFileSystemIdentity(string path)
    {
        using var handle = OpenDirectory(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
            throw new IOException("无法核验 AFDEV 文件系统身份。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

        return $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out DirectoryInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}
