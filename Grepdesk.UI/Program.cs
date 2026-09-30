using Avalonia;
using Grepdesk.UI.Startup;

namespace Grepdesk.UI;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
