namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Optional managed local MongoDB settings. Prefer an external instance and resolve paths against the
/// executable directory.
/// </summary>
public sealed class MongoLocalProcessOptions
{
    public bool AutoStart { get; set; } = true;
    public string Executable { get; set; } = "database/bin/mongod.exe";
    public string DataDirectory { get; set; } = "database/data";
    public int StartupTimeoutSeconds { get; set; } = 30;
    public int ShutdownTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Supports environment variables and full absolute paths independently of the launching process's
    /// working directory.
    /// </summary>
    public static string Resolve(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());

        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new ArgumentException("MongoDB 路径须为相对 EXE 的路径或完整绝对路径。");

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    /// <summary>
    /// Checks only path syntax and timeout ranges, without creating a database or moving data.
    /// </summary>
    public void Validate()
    {
        _ = Resolve(Executable);
        _ = Resolve(DataDirectory);

        if (StartupTimeoutSeconds is < 5 or > 300 || ShutdownTimeoutSeconds is < 5 or > 300)
            throw new InvalidDataException("MongoDB 启停超时须在 5 至 300 秒之间。");
    }
}
