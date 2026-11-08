using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AFConnect.Launcher;

internal static class LauncherDialogs
{
    internal static Task ShowAsync(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title, Width = 620, Height = 340, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var close = new Button { Content = "我知道了", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(24) };

        close.Classes.Add("primary");
        close.Click += (_, _) => dialog.Close();
        grid.Children.Add(new ScrollViewer
        {
            Content = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) }
        });
        Grid.SetRow(close, 1);
        grid.Children.Add(close);
        dialog.Content = grid;
        return dialog.ShowDialog(owner);
    }
}
