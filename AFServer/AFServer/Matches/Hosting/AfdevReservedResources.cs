using System.Buffers;
using System.Security.Cryptography;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Read-only lease of immutable in-place client resources. Validates file sets and contents, then prevents
/// overwrite/deletion without creating copies or links.
/// </summary>
internal sealed class AfdevReservedResources : IDisposable
{
    private readonly List<FileStream> handles = [];

    private AfdevReservedResources() { }

    /// <summary>
    /// Acquires resources while the caller owns the client-directory runtime reservation. Failure closes
    /// this call's handles; success retains them until the owned tree exits.
    /// </summary>
    internal static async Task<AfdevReservedResources> AcquireAsync(string root, RuntimeManifest manifest,
        CancellationToken cancellationToken = default)
    {
        manifest.ValidateMetadata();
        cancellationToken.ThrowIfCancellationRequested();

        var actual = AfdevReservedRuntimeFiles.Enumerate(root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = manifest.Files.Select(file => RuntimePath.NormalizeRelativePath(file.RelativePath));

        if (!actual.SetEquals(expected))
            throw new InvalidDataException($"AFDEV 预留客户端与 manifest 资源集合不一致：{root}");

        const int bufferSize = 128 * 1024;
        var resources = new AfdevReservedResources();
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = RuntimePath.ResolveUnderRoot(root, file.RelativePath);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1, FileOptions.Asynchronous | FileOptions.SequentialScan);

                resources.handles.Add(stream);

                if (stream.Length != file.Size)
                    throw new InvalidDataException($"AFDEV 预留资源长度与 manifest 不一致：{file.RelativePath}");

                // Reuse one large buffer for the entire lease to avoid small asynchronous I/O. Long-lived read-only handles retain no individual large buffers.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytesRead = 0;
                int count;

                while ((count = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    bytesRead = checked(bytesRead + count);
                    hash.AppendData(buffer, 0, count);
                }

                if (bytesRead != file.Size ||
                    !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"AFDEV 预留资源 SHA-256 与 manifest 不一致：{file.RelativePath}");
            }

            return resources;
        }
        catch
        {
            resources.Dispose();

            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Closes only this lease's read-only handles, never writing, renaming, or deleting reserved-client
    /// files.
    /// </summary>
    public void Dispose()
    {
        foreach (var handle in handles)
            handle.Dispose();

        handles.Clear();
    }
}
