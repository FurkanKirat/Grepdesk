using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Grepdesk.UI.Startup;

/// <summary>
/// Owns the primary process's command stream and open windows. Accepts both
/// the initial launch command and commands forwarded by subsequent launches
/// over <see cref="SingleInstanceHost"/>, and shuts the application down when
/// the last window closes and no job batches are pending.
/// </summary>
internal sealed class CommandDispatcher
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly SingleInstanceHost? _host;
    private readonly JobDispatcher _jobs;
    private readonly HashSet<Window> _openWindows = [];
    private readonly List<MainWindow> _mainWindows = [];
    private bool _reportedPreviousCrashes;

    public CommandDispatcher(IClassicDesktopStyleApplicationLifetime desktop, SingleInstanceHost? host)
    {
        _desktop = desktop;
        _host = host;
        _jobs = new JobDispatcher(TrackAndShow);
    }

    public void Start(AppCommand first)
    {
        Dispatch(first, fromIpc: false);

        if (_host is not null)
            _ = PumpAsync(_host);
    }

    private async Task PumpAsync(SingleInstanceHost source)
    {
        await foreach (var command in source.Commands.ReadAllAsync())
            Dispatcher.UIThread.Post(() => Dispatch(command, fromIpc: true));
    }

    private void Dispatch(AppCommand command, bool fromIpc)
    {
        if (fromIpc)
            SessionTrail.Note($"ipc command: {command.Kind}, {command.Paths.Count} path(s)");

        if (command.IsJob)
        {
            _jobs.Enqueue(command);
            return;
        }

        if (command.Kind == CommandKind.OpenFile && command.Paths.FirstOrDefault(File.Exists) is { } file)
        {
            OpenViewer(file);
            return;
        }

        if (command.Kind == CommandKind.OpenFolder && command.Paths.FirstOrDefault(Directory.Exists) is { } folder)
        {
            OpenMainWindow(folder);
            return;
        }

        // Plain start with no path (or a path that no longer exists): bring an
        // existing main window to the front, or open one if only jobs/viewers are up.
        if (fromIpc && _mainWindows.LastOrDefault() is { } existing)
        {
            RestoreAndFocus(existing);
            return;
        }

        OpenMainWindow(null);
    }

    private void OpenMainWindow(string? startFolder)
    {
        var window = new MainWindow(startFolder);
        _mainWindows.Add(window);
        _desktop.MainWindow ??= window;
        ReportPreviousCrashesOnce(window);
        TrackAndShow(window);
    }

    private void OpenViewer(string path)
    {
        var viewer = App.OpenStandaloneViewer(path);
        ReportPreviousCrashesOnce(viewer);
        TrackAndShow(viewer);
    }

    private void TrackAndShow(Window window)
    {
        _openWindows.Add(window);
        window.Closed += (_, _) =>
        {
            _openWindows.Remove(window);
            if (window is MainWindow mw)
            {
                _mainWindows.Remove(mw);
                if (ReferenceEquals(_desktop.MainWindow, mw))
                    _desktop.MainWindow = _mainWindows.LastOrDefault();
            }
            ShutdownIfIdle();
        };
        window.Show();
        window.Activate();
    }

    private void ReportPreviousCrashesOnce(Window window)
    {
        if (_reportedPreviousCrashes)
            return;
        _reportedPreviousCrashes = true;
        window.Opened += (_, _) => _ = CrashHandling.ReportPreviousCrashes();
    }

    private static void RestoreAndFocus(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private void ShutdownIfIdle()
    {
        if (_openWindows.Count > 0 || _jobs.HasPending)
            return;

        if (_host is not null)
        {
            // Release the pipe first so a launch arriving now starts a new
            // primary, then pick up anything that slipped in just before.
            _host.StopAccepting();
            var any = false;
            while (_host.Commands.TryRead(out var late))
            {
                Dispatch(late, fromIpc: true);
                any = true;
            }
            if (any)
                return;
        }

        // Low priority: a command the pump already posted runs first and
        // opens its window, and then we are no longer idle.
        Dispatcher.UIThread.Post(() =>
        {
            if (_openWindows.Count == 0 && !_jobs.HasPending)
                _desktop.Shutdown();
        }, DispatcherPriority.Background);
    }
}
