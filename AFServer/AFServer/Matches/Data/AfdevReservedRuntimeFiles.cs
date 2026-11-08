namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Selects immutable AFDEV resources from the existing server client. In-place runtime may retain native
/// logs and development attachments without authorizing them to execute.
/// </summary>
internal static class AfdevReservedRuntimeFiles
{
    private static readonly HashSet<string> AttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bak", ".id0", ".id1", ".id2", ".idb", ".i64", ".nam", ".til",
        ".log", ".dmp", ".dump", ".pcap", ".pcapng"
    };

    private static readonly HashSet<string> NativeDiagnosticPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "Binaries/Win32/bugreport.txt", "Binaries/Win32/CrashHandler.txt",
        "Binaries/Win32/CrashRecord.txt", "Binaries/Win32/rs_log.txt"
    };

    /// <summary>
    /// Enumerates all directories read-only and rejects reparse points. Returns resources for exact
    /// manifest comparison without following or deleting attachments.
    /// </summary>
    internal static IEnumerable<string> Enumerate(string root)
    {
        RuntimePath.RejectReparsePoints(root);

        var pending = new Stack<string>();

        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            RuntimePath.RejectReparsePoints(directory);

            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(path);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"AFDEV 预留客户端不允许重解析点：{path}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
                else
                {
                    var relativePath = RuntimePath.NormalizeRelativePath(Path.GetRelativePath(root, path));

                    if (IsAsset(relativePath))
                        yield return RuntimePath.ValidateRuntimeFilePath(relativePath);
                }
            }
        }
    }

    /// <summary>
    /// Excludes generated and auxiliary files only at known locations. DLLs, formal configuration, and
    /// static packages always remain manifest-controlled.
    /// </summary>
    internal static bool IsAsset(string relativePath)
    {
        var path = RuntimePath.NormalizeRelativePath(relativePath);
        var extension = Path.GetExtension(path);

        if (!path.StartsWith("Binaries/Win32/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("TGame/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (RuntimePath.IsAfdevExecutablePath(path))
            return true;
        if (AttachmentExtensions.Contains(extension) || NativeDiagnosticPaths.Contains(path) ||
            extension.StartsWith(".bak_", StringComparison.OrdinalIgnoreCase))
            return false;

        // UE3 generates UDK configuration from defaults; Default*.ini and Engine/Base*.ini still require byte-for-byte validation.
        if (path.StartsWith("TGame/Config/UDK", StringComparison.OrdinalIgnoreCase) &&
            extension.Equals(".ini", StringComparison.OrdinalIgnoreCase))
            return false;

        // Allow changes only to these explicit log/cache file types. Directory names cannot exempt added DLLs or CookedPC packages.
        if ((path.StartsWith("TGame/Logs/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("Engine/Logs/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("TGame/Cache/", StringComparison.OrdinalIgnoreCase)) &&
            (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".cache", StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }
}
