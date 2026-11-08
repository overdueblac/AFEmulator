using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AFServer.Avalonia;

internal static class Dialogs
{
    internal static Task ShowAsync(Window owner, string title, string message)
        => Create(title, message).ShowDialog(owner);

    internal static Window Create(string title, string message)
    {
        var window = new Window
        {
            Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#100B06"), Foreground = Brush.Parse("#EDAA58")
        };
        var close = new Button { Content = "确定", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };

        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 24,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close }
        };
        return window;
    }
}
