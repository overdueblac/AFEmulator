using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace AFConnect.Launcher.Maintenance;

/// <summary>
/// Repairs local mappings for only the three compatibility domains. Preserves other hosts aliases,
/// comments, and encoding, then flushes DNS through the system API.
/// </summary>
internal static partial class HostsFileRepair
{
    private static readonly string[] Names = ["tversion.levelupgames.ph", "tauthproxy.levelupgames.ph", "tdir.levelupgames.ph"];

    internal static async Task<string> RepairAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("修复 hosts 需要管理员权限。请退出后以管理员身份运行 AFConnect.GUI.exe。");

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        var update = RepairFile(path);
        string saved = !update.Changed ? "hosts 映射已正确，无需改写。"
            : update.Backup is null ? "hosts 原文件不存在，已创建并保存映射。" : $"hosts 已保存；原件备份：{update.Backup}";

        if (!DnsFlushResolverCache())
            throw new IOException(saved + Environment.NewLine + "DNS 缓存刷新失败，请检查系统 DNS 服务后重试。");

        foreach (string name in Names)
        {
            IPAddress[] addresses;

            try { addresses = await Dns.GetHostAddressesAsync(name).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) when (error is SocketException or TimeoutException)
            {
                throw new IOException(saved + Environment.NewLine + $"解析核验失败：{name}。请检查系统 DNS 服务后重试。", error);
            }

            var ipv4 = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();

            if (ipv4.Length == 0 || ipv4.Any(address => !address.Equals(IPAddress.Loopback)))
                throw new IOException(saved + Environment.NewLine + $"解析核验未通过：{name}。请检查安全软件或 DNS 拦截后重试。");
        }

        return saved + Environment.NewLine + "DNS 缓存已刷新，三个域名均核验为 127.0.0.1。";
    }

    internal static (bool Changed, string? Backup) RepairFile(string path)
    {
        bool existed = File.Exists(path);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        using var originalStream = new MemoryStream();

        stream.CopyTo(originalStream);

        byte[] original = originalStream.ToArray();
        byte[] updated = RepairBytes(original);

        if (original.AsSpan().SequenceEqual(updated)) return (false, null);

        string? backup = existed ? path + ".nzconnect-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")
            + "-" + Guid.NewGuid().ToString("N")[..8] + ".bak" : null;

        if (backup is not null)
        {
            using var backupStream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            backupStream.Write(original);
            backupStream.Flush(flushToDisk: true);
        }

        try
        {
            ReplaceContents(stream, updated);
        }
        catch (Exception writeError)
        {
            try { ReplaceContents(stream, original); }
            catch (Exception rollbackError)
            {
                throw new AggregateException($"hosts 写入与恢复均失败，请从备份恢复：{backup ?? "原 hosts 不存在"}", writeError, rollbackError);
            }

            throw;
        }

        return (true, backup);
    }

    internal static byte[] RepairBytes(byte[] bytes)
    {
        int offset = 0;
        Encoding encoding = Encoding.Latin1;

        if (bytes.AsSpan().StartsWith(new byte[] { 255, 254 })) { encoding = Encoding.Unicode; offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 254, 255 })) { encoding = Encoding.BigEndianUnicode; offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) offset = 3;

        string[] parts = LineEndings().Split(encoding.GetString(bytes, offset, bytes.Length - offset));

        for (int index = 0; index < parts.Length; index += 2)
        {
            string line = parts[index];
            int commentIndex = line.IndexOf('#');
            string comment = commentIndex < 0 ? "" : line[commentIndex..];
            string body = commentIndex < 0 ? line : line[..commentIndex];
            string[] fields = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length < 2) continue;

            string[] keep = fields.Skip(1).Where(alias => !Names.Contains(alias, StringComparer.OrdinalIgnoreCase)).ToArray();

            if (keep.Length == fields.Length - 1) continue;

            parts[index] = keep.Length > 0 ? fields[0] + "\t" + string.Join(' ', keep) + (comment.Length > 0 ? " " + comment : "") : comment;

            if (keep.Length == 0 && comment.Length == 0 && index + 1 < parts.Length) parts[index + 1] = "";
        }

        string result = string.Concat(parts);

        if (result.Length > 0 && result[^1] is not ('\r' or '\n')) result += "\r\n";

        foreach (string name in Names) result += "127.0.0.1 " + name + "\r\n";

        byte[] bodyBytes = encoding.GetBytes(result);
        byte[] output = new byte[offset + bodyBytes.Length];

        bytes.AsSpan(0, offset).CopyTo(output);
        bodyBytes.CopyTo(output, offset);
        return output;
    }

    private static void ReplaceContents(FileStream stream, byte[] bytes)
    {
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    [GeneratedRegex("(\r\n|\n|\r)")]
    private static partial Regex LineEndings();

    [DllImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
