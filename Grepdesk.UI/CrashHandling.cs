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
/// where the log is before the process ends. A crash in native code ends the
/// process before any of this runs; <see cref="SessionTrail"/> finds it at the
/// next start and <see cref="ReportPreviousCrashes"/> shows it.
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
            SessionTrail.End(); // logged and shown here; not a mystery crash next time
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
            SessionTrail.Note($"error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
            // A handler failing in a loop would otherwise stack up dialogs.
            if (_openDialog is null)
                ShowDialog(Text("ErrorTitle"), Text("ErrorMessage"), e.Exception.Message, e.Exception.ToString());
        };
    }

    /// <summary>
    /// Looks for runs that ended in a crash nothing could catch, logs them with
    /// Windows' record of the crash, and shows what was found. Off the UI thread:
    /// asking the event log takes a moment.
    /// </summary>
    public static async Task ReportPreviousCrashes()
    {
        List<SessionTrail.Crash> crashes;
        try
        {
            crashes = await Task.Run(() =>
            {
                var found = SessionTrail.TakeCrashes().Select(SessionTrail.WithSystemReport).ToList();
                foreach (var crash in found)
                    ErrorLog.Write("crash found at next start", crash.ToString());
                return found;
            });
        }
        catch (Exception e)
        {
            ErrorLog.Write(e, "crash report");
            return;
        }
        if (crashes.Count == 0 || _openDialog is not null) return;

        var newest = crashes.MaxBy(c => c.LastWriteUtc)!;
        var summary = newest.SystemReport?.Split('\n')[0].Trim()
                      ?? newest.Trail.LastOrDefault()
                      ?? "";
        ShowDialog(Text("CrashTitle"), Text("CrashMessage"), summary,
            string.Join(Environment.NewLine + Environment.NewLine, crashes.Select(c => c.ToString())));
    }

    /// <param name="summary">One line, shown as is.</param>
    /// <param name="details">The whole report: shown under "Details" and copied by its button.</param>
    private static void ShowDialog(string title, string message, string summary, string details)
    {
        var openLogs = new Button
        {
            Content = Text("ErrorOpenLogs"),
            Background = Brush.Parse("#313244"),
            Foreground = Brush.Parse("#cdd6f4"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6)
        };
        var copy = new Button
        {
            Content = Text("ErrorCopyDetails"),
            Background = Brush.Parse("#313244"),
            Foreground = Brush.Parse("#cdd6f4"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6)
        };
        var detailsBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            MaxHeight = 260,
            Background = Brush.Parse("#11111b"),
            Foreground = Brush.Parse("#a6adc8"),
            BorderBrush = Brush.Parse("#313244"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(detailsBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
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
            Title = title,
            Width = 560,
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
                    new TextBlock { Text = title, Foreground = Brush.Parse("#cdd6f4"), FontSize = 14, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = message, Foreground = Brush.Parse("#bac2de"), FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new SelectableTextBlock { Text = summary, Foreground = Brush.Parse("#7f849c"), FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    new Expander
                    {
                        Header = Text("ErrorDetails"),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = detailsBox
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 10, 0, 0),
                        Children = { copy, openLogs, close }
                    }
                }
            }
        };
        openLogs.Click += (_, _) => PlatformShellFactory.CreatePlatformShell().OpenPath(ErrorLog.Directory);
        copy.Click += async (_, _) =>
        {
            if (dialog.Clipboard is { } clipboard) await clipboard.SetTextAsync(details);
        };
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
            "ErrorCopyDetails" => "Copy details",
            "ErrorDetails" => "Details",
            "CrashTitle" => "Grepdesk closed unexpectedly",
            "CrashMessage" => "The last time Grepdesk ran, it stopped without warning. What it was doing and Windows' record of the crash were saved to the log folder.",
            _ => key
        };
    }

    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
