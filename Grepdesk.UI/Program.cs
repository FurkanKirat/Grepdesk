using Avalonia;
using Grepdesk.Core;
using Grepdesk.UI.Helpers;
using Grepdesk.UI.Startup;

namespace Grepdesk.UI;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CrashHandling.InstallProcessHandlers();

        // Run by the uninstaller: take every Explorer entry and file association
        // off before the exe is deleted, so nothing is left pointing at it.
        if (args is ["--unregister"])
        {
            if (PlatformShellFactory.CreateShellIntegration() is { } shell)
                foreach (var feature in Enum.GetValues<ShellFeature>())
                    shell.Disable(feature);
            return;
        }

        var command = AppCommand.Parse(args);

        SingleInstanceHost? host = null;
        if (command.IsJob)
        {
            host = SingleInstanceHost.TryAcquire();
            if (host is null)
            {
                // Another Grepdesk is collecting jobs: hand ours over and exit
                // before paying for Avalonia's startup.
                if (SingleInstanceHost.TrySend(command))
                    return;
                // The primary didn't answer (it may be shutting down); run the job ourselves.
            }
            else
            {
                host.StartListening();
            }
        }

        App.StartupCommand = command;
        App.Host = host;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
