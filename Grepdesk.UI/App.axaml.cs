using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Grepdesk.UI.Helpers;
using Grepdesk.UI.Startup;
using Grepdesk.UI.Viewer;

namespace Grepdesk.UI;

public class App : Application
{
    // Set by Program.Main before Avalonia starts.
    internal static AppCommand StartupCommand { get; set; } = new(CommandKind.OpenSearch, []);
    internal static SingleInstanceHost? Host { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CrashHandling.InstallDispatcherHandler();

        // Load strings for the chosen (or system) language before any window
        // is constructed, so XAML bindings resolve correctly on first render.
        // Falls back to en.json automatically if the language file isn't
        // found (see LocalizationService.Load).
        LocalizationService.Instance.Load(
            AppSettings.Current.Language ?? LocalizationService.DetectSystemLanguage());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Stay alive across window opens/closes (e.g. standalone viewers,
            // job windows, or multiple browser windows); CommandDispatcher shuts
            // the app down once the last tracked window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new CommandDispatcher(desktop, Host).Start(StartupCommand);
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static FileViewerWindow OpenStandaloneViewer(string path)
    {
        var shell = PlatformShellFactory.CreatePlatformShell();
        var viewer = new FileViewerWindow(standalone: true);
        viewer.OpenRequested += target => shell.OpenPath(target);
        viewer.ShowInFolderRequested += target => shell.ShowInFileManager(target);
        viewer.Opened += (_, _) => viewer.Load(path, null);
        return viewer;
    }
}