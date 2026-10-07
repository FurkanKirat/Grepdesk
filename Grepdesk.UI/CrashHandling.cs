using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Grepdesk.UI.Helpers;

namespace Grepdesk.UI;

/// <summary>
/// Last line of defence for errors nothing else caught (many UI handlers are
/// async void). Each one is logged; errors on the UI thread and in forgotten
/// tasks are survived with a short notice, and a fatal one at least says
/// where the log is before the process ends.
/// </summary>
internal static class CrashHandling
{
    private static LocalizationService Loc => LocalizationService.Instance;
    private static Window? _openDialog;

    // Avalonia also stores a UI-thread exception in a task that later surfaces
    // as unobserved; this keeps it from being logged twice.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, object> Logged = new();

    /// <summary>Before Avalonia starts: covers every thread for the whole run.</summary>
    public static void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is not Exception exception) return;
            ErrorLog.Write(exception, "fatal");
            if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, $"{Text("ErrorFatal")}\n\n{exception.Message}\n\n{ErrorLog.Directory}", "Grepdesk", MB_ICONERROR);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (!e.Exception.InnerExceptions.All(inner => Logged.TryGetValue(inner, out _)))
                ErrorLog.Write(e.Exception, "unobserved task");
            e.SetObserved();
        };
    }

    /// <summary>Once Avalonia is up: errors thrown by UI event handlers and dispatcher work.</summary>
    public static void InstallDispatcherHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ErrorLog.Write(e.Exception, "UI thread");
            Logged.AddOrUpdate(e.Exception, true);
            e.Handled = true;
            ShowNotice(e.Exception);
        };
    }

    private static void ShowNotice(Exception exception)
    {
        // A handler failing in a loop would otherwise stack up dialogs.
        if (_openDialog is not null) return;

        var openLogs = new Button
        {
            Content = Text("ErrorOpenLogs"),
            Background = Brush.Parse("#313244"),
            Foreground = Brush.Parse("#cdd6f4"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6)
        };
        var close = new Button
        {
            Content = Text("ErrorClose"),
            Background = Brush.Parse("#89b4fa"),
            Foreground = Brush.Parse("#11111b"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6),
            IsDefault = true,
            IsCancel = true
        };

        var dialog = new Window
        {
            Title = Text("ErrorTitle"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brush.Parse("#1e1e2e"),
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = Text("ErrorTitle"), Foreground = Brush.Parse("#cdd6f4"), FontSize = 14, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = Text("ErrorMessage"), Foreground = Brush.Parse("#bac2de"), FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new SelectableTextBlock { Text = exception.Message, Foreground = Brush.Parse("#7f849c"), FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 10, 0, 0),
                        Children = { openLogs, close }
                    }
                }
            }
        };
        openLogs.Click += (_, _) => PlatformShellFactory.CreatePlatformShell().OpenPath(ErrorLog.Directory);
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => _openDialog = null;

        _openDialog = dialog;
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is { IsVisible: true }) dialog.Show(owner);
        else dialog.Show();
    }

    // Strings may be needed before the language files are loaded (or if loading them failed).
    private static string Text(string key)
    {
        var text = Loc.Get(key);
        return text != key ? text : key switch
        {
            "ErrorFatal" => "Grepdesk ran into an unexpected error and has to close. The details were saved to the log folder:",
            "ErrorTitle" => "Something went wrong",
            "ErrorMessage" => "Grepdesk ran into an unexpected error and kept running. If something looks off, restart the app.",
            "ErrorOpenLogs" => "Open log folder",
            "ErrorClose" => "Close",
            _ => key
        };
    }

    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
