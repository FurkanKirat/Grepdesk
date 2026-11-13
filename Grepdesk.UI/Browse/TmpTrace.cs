// TMP-TRACE: temporary timing, removed after the measurement
using System.Diagnostics;
using Avalonia.Threading;
namespace Grepdesk.UI.Browse;
internal static class Tr
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grepdesk-trace.log");
    private static long _lastTick;
    private static DispatcherTimer? _timer;
    public static int Prepared, Thumbs;
    public static void Log(string text)
    {
        lock (Clock) File.AppendAllText(Path, $"{Clock.ElapsedMilliseconds,7} ms [t{Environment.CurrentManagedThreadId}] {text}\n");
    }
    public static void StartStallMonitor()
    {
        if (_timer is not null) return;
        _lastTick = Clock.ElapsedMilliseconds;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            var now = Clock.ElapsedMilliseconds;
            if (now - _lastTick > 250) Log($"UI STALL {now - _lastTick} ms");
            _lastTick = now;
        });
        _timer.Start();
    }
}
