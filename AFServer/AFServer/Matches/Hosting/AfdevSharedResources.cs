using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Links validated static UE3 packages into a legacy instance, using file-sharing protection until all
/// users exit.
/// </summary>
/// <remarks>
/// Each preflight result shares one set of read handles. The last lease closes them; the next acquisition
/// revalidates contents.
/// </remarks>
internal sealed class AfdevSharedResources : IDisposable
{
    private static readonly ConditionalWeakTable<AfdevRuntimeValidation, SharedSet> Sets = new();
    private readonly object gate = new();
    private readonly SharedSet resources;
    private bool disposed;

    private AfdevSharedResources(SharedSet resources)
    {
        this.resources = resources;
        SharedFileCount = resources.SharedFiles.Count;
        SharedBytes = resources.SharedFiles.Values.Aggregate(0L, (total, file) => checked(total + file.Size));
    }

    /// <summary>Static files protected by this lease.</summary>
    internal int SharedFileCount { get; }

    /// <summary>
    /// Logical shared-file bytes. Instance creation allocates no additional data blocks for them.
    /// </summary>
    internal long SharedBytes { get; }

    /// <summary>
    /// Shares only static CookedPC packages. Executables, configuration, caches, and unknown types remain
    /// isolated per legacy instance.
    /// </summary>
    internal static bool IsShared(RuntimeManifestFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var path = RuntimePath.ValidateRuntimeFilePath(file.RelativePath);
        var extension = Path.GetExtension(path);

        return (path.StartsWith("TGame/CookedPC/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Engine/CookedPC/", StringComparison.OrdinalIgnoreCase)) &&
            (extension.Equals(".u", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".upk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".udk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tfc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Acquires static-resource protection. First acquisition verifies every static hash; overlapping
    /// leases reuse the same validated handles.
    /// </summary>
    /// <remarks>
    /// Failure/cancellation releases newly opened handles. A successful instance retains them until
    /// native-process exit and owned-directory cleanup.
    /// </remarks>
    internal static async Task<AfdevSharedResources> AcquireAsync(AfdevRuntimeValidation validation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validation);
        cancellationToken.ThrowIfCancellationRequested();

        validation.Manifest.ValidateMetadata();
        ValidateStorage(validation.Options);

        var resources = Sets.GetValue(validation, current => new SharedSet(current));

        await resources.Gate.WaitAsync(cancellationToken);

        try
        {
            resources.ValidateSnapshot(validation);

            if (resources.References == 0)
                await resources.OpenVerifiedAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var lease = new AfdevSharedResources(resources);

            resources.References = checked(resources.References + 1);

            return lease;
        }
        catch
        {
            if (resources.References == 0)
                resources.CloseHandles();

            throw;
        }
        finally { resources.Gate.Release(); }
    }

    /// <summary>
    /// Checks local NTFS and same-volume requirements for legacy resource sharing without creating
    /// directories, links, or copies.
    /// </summary>
    internal static void ValidateStorage(MatchRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AFDEV 静态资源共享要求 Windows 本地 NTFS 卷。");

        EnsureLocalNtfs(options.AfdevRuntimeRoot);
        EnsureLocalNtfs(options.InstancesRoot);

        var source = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, options.AfdevRuntimeRoot);
        var destination = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, options.InstancesRoot);

        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("AFDEV 静态资源共享不能跨卷：请将 AfdevRuntimeRoot 与 InstancesRoot 配置在同一本地 NTFS 卷，不会回退复制完整运行包。");
    }

    /// <summary>
    /// Creates a same-volume hard link for a static file and checks file identity, never falling back to
    /// large-file copying.
    /// </summary>
    /// <remarks>
    /// Deleting the link leaves source files and other instances intact. Protection handles allow link
    /// deletion but reject write handles through any link.
    /// </remarks>
    internal void LinkTo(string destinationRoot, RuntimeManifestFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var relativePath = RuntimePath.ValidateRuntimeFilePath(file.RelativePath);

            if (!resources.SharedFiles.TryGetValue(relativePath, out var expected) || !expected.Matches(file) ||
                !resources.Handles.TryGetValue(relativePath, out var resource))
                throw new InvalidDataException($"AFDEV 静态资源与获取时的清单不一致：{relativePath}");

            var source = RuntimePath.ResolveUnderRoot(resources.SourceRoot, relativePath);
            var destination = RuntimePath.ResolveUnderRoot(destinationRoot, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            RuntimePath.RejectReparsePoints(destination);

            if (!CreateHardLink(destination, source, IntPtr.Zero))
                throw new IOException($"AFDEV 静态资源硬链接创建失败：{relativePath}。请将 AfdevRuntimeRoot 与 InstancesRoot 放在同一本地 NTFS 卷，确认目录权限及硬链接数量；不会回退复制完整运行包。",
                    new Win32Exception(Marshal.GetLastWin32Error()));

            RuntimePath.RejectReparsePoints(destination);

            using var linkedFile = new FileStream(destination, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 1);

            if (ReadIdentity(linkedFile.SafeFileHandle) != resource.Identity)
                throw new InvalidDataException($"AFDEV 静态资源源路径已被替换，链接身份与已核验文件不一致：{relativePath}");

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Releases this lease safely on repeated calls, preserving shared read handles and write exclusion
    /// while other instances still use them.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;

            resources.Gate.Wait();

            try
            {
                if (--resources.References == 0)
                    resources.CloseHandles();

                disposed = true;
            }
            finally { resources.Gate.Release(); }
        }
    }

    private static void EnsureLocalNtfs(string path)
    {
        var fullPath = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, path);
        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);

        RuntimePath.RejectReparsePoints(fullPath);

        if (drive.DriveType == DriveType.Network || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("AFDEV 静态资源共享要求运行包和实例目录位于同一本地 NTFS 卷；不支持网络共享或不具备对应文件保护语义的文件系统。");
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("无法核验 AFDEV 静态资源的文件系统身份。", new Win32Exception(Marshal.GetLastWin32Error()));

        if ((information.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("AFDEV 静态资源不能是目录或重解析点。");

        return new FileIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }

    private sealed class SharedSet
    {
        private readonly Dictionary<string, FileSnapshot> manifestFiles;

        internal SharedSet(AfdevRuntimeValidation validation)
        {
            SourceRoot = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, validation.Options.AfdevRuntimeRoot);
            manifestFiles = validation.Manifest.Files.ToDictionary(
                file => RuntimePath.ValidateRuntimeFilePath(file.RelativePath),
                file => new FileSnapshot(file.Size, file.Sha256), StringComparer.OrdinalIgnoreCase);
            SharedFiles = validation.Manifest.Files.Where(IsShared).ToDictionary(
                file => RuntimePath.ValidateRuntimeFilePath(file.RelativePath),
                file => new FileSnapshot(file.Size, file.Sha256), StringComparer.OrdinalIgnoreCase);
        }

        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal string SourceRoot { get; }
        internal Dictionary<string, FileSnapshot> SharedFiles { get; }
        internal Dictionary<string, SharedFile> Handles { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int References { get; set; }

        internal void ValidateSnapshot(AfdevRuntimeValidation validation)
        {
            var currentRoot = RuntimePath.ResolveConfiguredPath(AppContext.BaseDirectory, validation.Options.AfdevRuntimeRoot);

            if (!currentRoot.Equals(SourceRoot, StringComparison.OrdinalIgnoreCase) ||
                validation.Manifest.Files.Count != manifestFiles.Count ||
                validation.Manifest.Files.Any(file =>
                    !manifestFiles.TryGetValue(RuntimePath.ValidateRuntimeFilePath(file.RelativePath), out var expected) ||
                    !expected.Matches(file)))
                throw new InvalidDataException("AFDEV 预检结果在首次共享资源获取后被修改；请对新的部署重新执行预检。");
        }

        internal async Task OpenVerifiedAsync(CancellationToken cancellationToken)
        {
            // Reuse a large read buffer for the batch to avoid fine-grained asynchronous I/O per static package. Retained streams hold no file buffer.
            const int bufferSize = 1024 * 1024;
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            try
            {
                foreach (var (relativePath, expected) in SharedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = RuntimePath.ResolveUnderRoot(SourceRoot, relativePath);
                    // Exclude write sharing; NTFS applies shared-access checks across every hard link to a file.
                    // Allow individual link deletion so stopped legacy instances can clean up independently while others run.
                    var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        1, FileOptions.Asynchronous | FileOptions.SequentialScan);

                    try
                    {
                        if (stream.Length != expected.Size)
                            throw new InvalidDataException($"AFDEV 静态资源在预检后改变长度：{relativePath}");

                        var identity = ReadIdentity(stream.SafeFileHandle);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long bytesRead = 0;
                        int count;

                        while ((count = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken)) != 0)
                        {
                            bytesRead += count;
                            hash.AppendData(buffer, 0, count);
                        }

                        if (bytesRead != expected.Size ||
                            !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"AFDEV 静态资源内容与预检清单不一致：{relativePath}");

                        Handles.Add(relativePath, new SharedFile(stream, identity));
                    }
                    catch
                    {
                        stream.Dispose();

                        throw;
                    }
                }
            }
            catch
            {
                CloseHandles();

                throw;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        internal void CloseHandles()
        {
            foreach (var resource in Handles.Values)
                resource.Stream.Dispose();

            Handles.Clear();
        }
    }

    private sealed record FileSnapshot(long Size, string Sha256)
    {
        internal bool Matches(RuntimeManifestFile file) => file.Size == Size &&
            Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SharedFile(FileStream Stream, FileIdentity Identity);
    private readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existingPath, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
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
