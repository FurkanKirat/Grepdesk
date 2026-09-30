using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Grepdesk.UI;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Load strings for the current system language before any window
        // is constructed, so XAML bindings resolve correctly on first render.
        // Falls back to en.json automatically if the system language file
        // isn't found (see LocalizationService.Load).
        LocalizationService.Instance.Load(LocalizationService.DetectSystemLanguage());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Explorer's "Open with Grepdesk" context menu passes the folder
            // as the first argument (see tools/install-context-menu.ps1).
            var startFolder = desktop.Args?.FirstOrDefault(Directory.Exists);
            desktop.MainWindow = new MainWindow(startFolder);
        }

        base.OnFrameworkInitializationCompleted();
    }
}