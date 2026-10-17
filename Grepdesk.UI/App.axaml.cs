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
            if (StartupCommand.IsJob)
            {
                // Job mode: no search window; the process lives as long as
                // its job windows do.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                new JobDispatcher(desktop, Host).Start(StartupCommand);
            }
            else if (StartupCommand.Paths.FirstOrDefault(File.Exists) is { } file)
            {
                // A file opened with Grepdesk (its menu entry, "Open with", or
                // Grepdesk as the file type's default app): just the viewer.
                desktop.MainWindow = OpenStandaloneViewer(file);
            }
            else
            {
                // Explorer's "Open with Grepdesk" passes the folder as the argument.
                var startFolder = StartupCommand.Paths.FirstOrDefault(Directory.Exists);
                desktop.MainWindow = new MainWindow(startFolder);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static FileViewerWindow OpenStandaloneViewer(string path)
    {
        var shell = PlatformShellFactory.CreatePlatformShell();
        var viewer = new FileViewerWindow(standalone: true);
        viewer.OpenRequested += target => shell.OpenPath(target);
        viewer.ShowInFolderRequested += target => shell.ShowInFileManager(target);
        viewer.Opened += (_, _) => viewer.Load(path, null);
        return viewer;
    }
}