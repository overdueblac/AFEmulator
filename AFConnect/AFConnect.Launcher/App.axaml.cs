using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AFConnect.Launcher;

/// <summary>
/// Composes the desktop application. The shared connection session manages all proxy, authentication, and
/// game lifetimes.
/// </summary>
public partial class App : Application
{
    /// <summary>Loads the application theme and shared control styles.</summary>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Creates the main window once the desktop lifetime is ready.</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new LauncherWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
