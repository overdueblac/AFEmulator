namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Runtime-package path boundary, rejecting traversal, device names, and reparse points under Windows
/// filename semantics.
/// </summary>
public static class RuntimePath
{
    /// <summary>
    /// Relative main AFDEV executable path, also the default entry point for legacy configuration and
    /// instances.
    /// </summary>
    public const string AfdevRelativePath = "Binaries/Win32/TGame_AFDEV.exe";

    /// <summary>Relative executable path for the second native host sharing client resources.</summary>
    public const string AfdevSecondaryRelativePath = "Binaries/Win32/TGame_AFDEV2.exe";

    /// <summary>
    /// Accepts only the two prebuilt AFDEV entry points, never authorizing player TGame.exe or other
    /// programs as a server.
    /// </summary>
    public static bool IsAfdevExecutablePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');

        return string.Equals(normalized, AfdevRelativePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, AfdevSecondaryRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".mst",
        ".bat", ".cmd", ".ps1", ".ps2", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".py", ".pyw", ".sh", ".bash", ".lnk", ".url", ".reg",
        ".log", ".bak", ".id0", ".id1", ".id2", ".idb", ".i64", ".nam", ".til",
        ".pem", ".key", ".pfx", ".p12", ".pid", ".dmp", ".dump", ".pcap", ".pcapng"
    };

    private static readonly HashSet<string> ForbiddenFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "state.json", "ready.json", "allocation.json", "bugreport.txt",
        "CrashHandler.txt", "CrashRecord.txt", "rs_log.txt"
    };

    /// <summary>
    /// Normalizes manifest paths to slash-separated relative paths, rejecting ADS, traversal, and Windows
    /// aliases.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("AFDEV 清单路径不能为空。");

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/');

        if (Path.IsPathRooted(normalized) || segments.Any(IsInvalidSegment))
            throw new InvalidDataException($"AFDEV 清单包含不安全的相对路径：{relativePath}");

        return normalized;
    }

    /// <summary>
    /// Validates runtime file locations and executable-content boundaries. Complete hash manifests
    /// constrain DLLs and UE3 script packages.
    /// </summary>
    public static string ValidateRuntimeFilePath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var allowedRoot = normalized.StartsWith("Binaries/Win32/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("TGame/", StringComparison.OrdinalIgnoreCase);

        if (!allowedRoot)
            throw new InvalidDataException($"AFDEV 清单文件不属于运行资源树：{relativePath}");
        if (ForbiddenExtensions.Contains(Path.GetExtension(normalized)) &&
            !IsAfdevExecutablePath(normalized))
            throw new InvalidDataException($"AFDEV 运行包不允许附加程序、脚本、开发产物或凭据：{relativePath}");

        var fileName = Path.GetFileName(normalized);

        if (ForbiddenFileNames.Contains(fileName) || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            normalized.Split('/').Any(segment => segment.Equals("TenproxyLog", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("tlog", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"AFDEV 运行包不允许日志、实例状态或凭据文件：{relativePath}");

        return normalized;
    }

    /// <summary>
    /// Resolves a relative path safely within the runtime root and checks all existing reparse points,
    /// including ancestors of the root.
    /// </summary>
    /// <remarks>
    /// The caller must exclusively own deployment-tree modifications. Path checks cannot stop another
    /// process from replacing a directory after validation.
    /// </remarks>
    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fullRoot = ResolveConfiguredPath(AppContext.BaseDirectory, root);
        var fullPath = Path.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar), fullRoot);

        if (!IsWithinRoot(fullRoot, fullPath))
            throw new InvalidDataException($"AFDEV 资源路径逃逸运行根：{relativePath}");

        RejectReparsePoints(fullPath);

        return fullPath;
    }

    /// <summary>
    /// Tests equality or containment within a root using Windows case-insensitive boundary comparisons.
    /// </summary>
    public static bool IsWithinRoot(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(ResolveConfiguredPath(AppContext.BaseDirectory, root));
        var fullPath = Path.TrimEndingDirectorySeparator(ResolveConfiguredPath(AppContext.BaseDirectory, path));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;

        return string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects symbolic links, junctions, and other reparse points in the path and every existing parent.
    /// </summary>
    public static void RejectReparsePoints(string path)
    {
        var current = ResolveConfiguredPath(AppContext.BaseDirectory, path);

        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"AFDEV 路径不允许重解析点：{current}");
            }
            catch (FileNotFoundException)
            {
                // A new target may not exist yet; still check every parent directory.
            }
            catch (DirectoryNotFoundException)
            {
                // Do not rely on File.Exists, which can miss dangling links with reparse attributes.
            }

            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }

    /// <summary>
    /// Resolves configuration paths against an absolute base, rejecting drive-relative paths, ADS, and
    /// device paths.
    /// </summary>
    public static string ResolveConfiguredPath(string baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(baseDirectory))
            throw new InvalidDataException("AFDEV 配置路径或基准目录无效。");
        if (IsDevicePath(path) || IsDevicePath(baseDirectory) ||
            (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException($"AFDEV 配置不允许设备路径或驱动器相对路径：{path}");

        var fullPath = Path.GetFullPath(path, baseDirectory);
        var pathRoot = Path.GetPathRoot(fullPath)!;
        var relativePart = fullPath[pathRoot.Length..];

        if (pathRoot.Length > 3 && pathRoot.Contains(':'))
            throw new InvalidDataException($"AFDEV 配置不允许 UNC 根中的备用数据流：{path}");
        if (relativePart.Length > 0)
            NormalizeRelativePath(relativePart);

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsDevicePath(string path)
    {
        var normalized = path.Replace('/', '\\');

        return normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    private static bool IsInvalidSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') ||
            char.IsWhiteSpace(segment[^1]) || segment.Any(character => char.IsControl(character) ||
                character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
            return true;

        var stem = segment.Split('.')[0];

        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3]));
    }
}
