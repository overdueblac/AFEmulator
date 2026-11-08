using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AFConnect.Launcher;

/// <summary>
/// Updates only user-editable connection fields. Replaces configuration atomically while preserving
/// certificates, fingerprints, and unknown extension fields.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly TextBox host = new();
    private readonly TextBox port = new();
    private readonly TextBox gamePath = new();
    private readonly TextBlock error = new() { Foreground = Brush.Parse("#B54115"), TextWrapping = TextWrapping.Wrap };

    internal SettingsWindow()
    {
        Title = "AF2014 · 连接设置";
        Width = 560;
        Height = 490;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var form = new StackPanel { Spacing = 10, Margin = new Thickness(28) };
        var save = new Button { Content = "保存设置", HorizontalAlignment = HorizontalAlignment.Right };

        save.Classes.Add("primary");
        save.Click += (_, _) => Save();
        form.Children.Add(new TextBlock { Text = "连接设置", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        AddField(form, "服务端地址", host);
        AddField(form, "CONNECT 端口", port);
        AddField(form, "公共客户端目录（相对启动器 EXE）", gamePath);
        form.Children.Add(new TextBlock
        {
            Text = "例如 ../AF_2014。证书、公钥与端口配对等高级配置保留在 config.json 中；本界面保存时不会覆盖这些字段。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#77746E"), Margin = new Thickness(0, 4)
        });
        form.Children.Add(error);
        form.Children.Add(save);
        Content = form;

        try
        {
            JsonObject config = Read();

            host.Text = config["ServerHost"]?.GetValue<string>() ?? "127.0.0.1";
            port.Text = config["ServerPort"]?.ToString() ?? "9443";
            gamePath.Text = config["GamePath"]?.GetValue<string>() ?? "../AF_2014";
        }
        catch (Exception exception) { error.Text = exception.Message; }
    }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private static JsonObject Read() => JsonNode.Parse(File.ReadAllText(ConfigPath), documentOptions:
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject()
        ?? throw new InvalidDataException("连接配置为空。");

    private static void AddField(StackPanel form, string label, TextBox field)
    {
        form.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        form.Children.Add(field);
    }

    private void Save()
    {
        string temporary = ConfigPath + ".tmp";

        try
        {
            string hostname = host.Text?.Trim() ?? "";
            string path = gamePath.Text?.Trim() ?? "";

            if (Uri.CheckHostName(hostname) == UriHostNameType.Unknown || !int.TryParse(port.Text, out int number) || number is < 1 or > 65535)
                throw new InvalidDataException("请输入有效服务端地址和 1–65535 内的端口。");
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                throw new InvalidDataException("客户端目录必须使用相对路径，例如 ../AF_2014。");

            JsonObject config = Read();

            config["ServerHost"] = hostname;
            config["ServerPort"] = number;
            config["GamePath"] = path.Replace('\\', '/');
            File.WriteAllText(temporary, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ConfigPath, overwrite: true);
            Close();
        }
        catch (Exception exception) { error.Text = exception.Message; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
