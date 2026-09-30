using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Grepdesk.Core.Jobs;

namespace Grepdesk.UI.Jobs;

/// <summary>
/// Small progress window for one zip / compress / paste job: progress,
/// speed, time left, pause and cancel. Closes itself when the job finishes
/// cleanly; stays open with the list of skipped files otherwise.
/// </summary>
public partial class JobWindow : Window
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly JobSpec _spec;
    private readonly CancellationTokenSource _cts = new();
    private readonly JobContext _context;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Stopwatch _clock = new();

    private long _lastDoneBytes;
    private TimeSpan _lastTick;
    private double _bytesPerSecond;
    private bool _finished;
    private bool _closeWhenFinished;

    public JobWindow() : this(JobSpec.Empty) { }

    public JobWindow(JobSpec spec)
    {
        InitializeComponent();
        _spec = spec;
        _context = new JobContext(new DialogConflictResolver(this), _cts.Token);

        Title = spec.Title;
        TitleText.Text = spec.Title;
        StatsText.Text = Loc.Get("JobPreparing");
        Bar.IsIndeterminate = true;
        PauseButton.Content = Loc.Get("JobPause");
        CancelButton.Content = Loc.Get("JobCancel");

        PauseButton.Click += (_, _) => TogglePause();
        CancelButton.Click += (_, _) =>
        {
            if (_finished) Close();
            else RequestCancel();
        };
        _timer.Tick += (_, _) => UpdateProgress();
        Closing += OnClosing;

        if (!ReferenceEquals(spec, JobSpec.Empty))
            Opened += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        _clock.Start();
        _timer.Start();

        var cancelled = false;
        try
        {
            // Engines do blocking I/O; keep them off the UI thread entirely.
            await Task.Run(() => _spec.Run(_context));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _context.ReportError(_spec.Title, ex);
        }

        _clock.Stop();
        _timer.Stop();
        _finished = true;
        UpdateProgress();

        if (_spec.Benchmark)
            BenchmarkLog.Write(_spec, _clock.Elapsed, _context.Progress.Snapshot(), _context.Errors.Count);

        if (_closeWhenFinished || _spec.Benchmark || _context.Errors.Count == 0)
        {
            Close();
            return;
        }

        ShowErrors(cancelled);
    }

    private void TogglePause()
    {
        if (_context.Pause.IsPaused)
        {
            _context.Pause.Resume();
            _clock.Start();
            PauseButton.Content = Loc.Get("JobPause");
        }
        else
        {
            _context.Pause.Pause();
            _clock.Stop();
            PauseButton.Content = Loc.Get("JobResume");
        }
    }

    private void RequestCancel()
    {
        _cts.Cancel();
        _context.Pause.Resume(); // let paused workers see the cancellation
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StatsText.Text = Loc.Get("JobCancelling");
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_finished)
            return;

        // Closing mid-job cancels it; the window goes away once the engine
        // has cleaned up its half-written file.
        e.Cancel = true;
        _closeWhenFinished = true;
        RequestCancel();
    }

    private void UpdateProgress()
    {
        var snap = _context.Progress.Snapshot();
        var now = _clock.Elapsed;

        var dt = (now - _lastTick).TotalSeconds;
        if (dt > 0.05)
        {
            var instant = (snap.DoneBytes - _lastDoneBytes) / dt;
            _bytesPerSecond = _bytesPerSecond == 0 ? instant : _bytesPerSecond * 0.8 + instant * 0.2;
            _lastDoneBytes = snap.DoneBytes;
            _lastTick = now;
        }

        if (snap.CurrentItem is { } current)
            CurrentText.Text = current;

        Bar.IsIndeterminate = !snap.TotalFinal && snap.TotalBytes == 0;
        Bar.Value = _finished ? 1 : snap.Fraction;

        if (_context.Token.IsCancellationRequested && !_finished)
            return;

        var stats = Loc.Get("JobStats",
            snap.DoneFiles.ToString("N0"), snap.TotalFiles.ToString("N0"),
            Format.Size(snap.DoneBytes), Format.Size(snap.TotalBytes));

        if (!_finished && !_context.Pause.IsPaused && _bytesPerSecond > 0)
        {
            stats += " · " + Format.Size((long)_bytesPerSecond) + "/s";
            if (snap.TotalFinal && snap.TotalBytes > snap.DoneBytes)
            {
                var left = TimeSpan.FromSeconds((snap.TotalBytes - snap.DoneBytes) / _bytesPerSecond);
                stats += " · " + Loc.Get("JobTimeLeft", Format.Duration(left));
            }
        }
        else if (_finished)
        {
            stats += " · " + Format.Duration(_clock.Elapsed);
        }

        StatsText.Text = stats;
    }

    private void ShowErrors(bool cancelled)
    {
        Bar.IsVisible = false;
        PauseButton.IsVisible = false;
        CancelButton.IsEnabled = true;
        CancelButton.Content = Loc.Get("JobClose");
        CurrentText.Text = cancelled ? Loc.Get("JobCancelled") : Loc.Get("JobFinishedWithErrors");

        ErrorsHeader.Text = Loc.Get("JobErrorsHeader", _context.Errors.Count);
        ErrorsList.ItemsSource = _context.Errors.Select(Describe).ToList();
        ErrorsPanel.IsVisible = true;
    }

    private static string Describe(JobError error)
    {
        var reason = error.Kind switch
        {
            JobErrorKind.UnsafePath => Loc.Get("JobErrorUnsafePath"),
            JobErrorKind.Encrypted => Loc.Get("JobErrorEncrypted"),
            JobErrorKind.Locked => Loc.Get("JobErrorLocked"),
            JobErrorKind.IntoItself => Loc.Get("JobErrorIntoItself"),
            JobErrorKind.LinkSkipped => Loc.Get("JobErrorLinkSkipped"),
            JobErrorKind.NotFound => Loc.Get("JobErrorNotFound"),
            _ => error.Detail ?? ""
        };
        return $"{error.Path} — {reason}";
    }
}
