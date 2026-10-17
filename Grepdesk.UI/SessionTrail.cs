using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Grepdesk.UI;

/// <summary>
/// The last things this process did (file opened, window maximized, PDF page
/// drawn...), kept in a small file next to the error log. A crash inside
/// native code (PDFium, Media Foundation, a graphics driver) ends the process
/// before any handler runs, so nothing else gets written. A normal exit
/// deletes the file, so one left behind marks a crash. The next start reports
/// it together with Windows' own record of the crash.
/// </summary>
internal static class SessionTrail
{
    private const int KeepLines = 80;
    private const string Pattern = "session-*.trail";
    private static readonly Lock Gate = new();
    private static readonly TrailLines Lines = new(KeepLines);
    private static string? _path;
    private static string _header = "";

    /// <summary>A process that ended without closing its trail.</summary>
    public sealed record Crash(int ProcessId, DateTime StartedUtc, DateTime LastWriteUtc, IReadOnlyList<string> Trail)
    {
        /// <summary>What Windows recorded about it, if it recorded anything.</summary>
        public string? SystemReport { get; init; }

        public override string ToString()
        {
            var text = new StringBuilder()
                .AppendLine($"Process {ProcessId}, started {StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, last activity {LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            if (SystemReport is not null) text.AppendLine().AppendLine(SystemReport.TrimEnd());
            text.AppendLine().AppendLine("Last steps:");
            foreach (var line in Trail) text.AppendLine("  " + line);
            return text.ToString();
        }
    }

    public static void Start(string? directory = null)
    {
        directory ??= ErrorLog.Directory;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            using var self = Process.GetCurrentProcess();
            lock (Gate)
            {
                _path = Path.Combine(directory, $"session-{Environment.ProcessId}.trail");
                _header = $"{Environment.ProcessId} {self.StartTime.ToUniversalTime():O}";
                Lines.Clear();
            }
            Note($"start: Grepdesk {AppInfo.Display}");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
        }
        catch
        {
            _path = null; // no trail is better than failing startup
        }
    }

    /// <summary>Numbers are written the same way whatever the user's language.</summary>
    public static void Note(FormattableString text)
    {
        lock (Gate)
        {
            if (_path is null) return;
            Lines.Add($"{DateTime.Now:HH:mm:ss.fff}", text.ToString(CultureInfo.InvariantCulture));
            try { File.WriteAllLines(_path, Lines.Prepend(_header)); }
            catch { /* a full disk must not break what the user is doing */ }
        }
    }

    /// <summary>Notes when a window opens, closes and is maximized or restored: resizing redraws everything in it.</summary>
    public static void Track(Avalonia.Controls.Window window, string name)
    {
        window.Opened += (_, _) => Note($"{name}: opened");
        window.Closed += (_, _) => Note($"{name}: closed");
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Window.WindowStateProperty)
                Note($"{name}: {e.NewValue}");
        };
    }

    /// <summary>
    /// The last lines, a step repeated back to back kept as one line with a
    /// count: a burst of the same step (a page asked for on every scroll event)
    /// must not push what came before it out of the trail.
    /// </summary>
    internal sealed class TrailLines(int keep) : IEnumerable<string>
    {
        private readonly List<string> _lines = new();
        private string? _lastText;
        private int _repeats;

        public void Add(string time, string text)
        {
            if (text == _lastText && _lines.Count > 0)
            {
                _repeats++;
                _lines[^1] = $"{time} {text} (×{_repeats})";
                return;
            }
            _lastText = text;
            _repeats = 1;
            _lines.Add($"{time} {text}");
            if (_lines.Count > keep) _lines.RemoveAt(0);
        }

        public void Clear()
        {
            _lines.Clear();
            _lastText = null;
        }

        public IEnumerator<string> GetEnumerator() => _lines.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The process closes normally, or its crash was already shown: nothing to report next time.</summary>
    public static void End()
    {
        lock (Gate)
        {
            if (_path is null) return;
            try { File.Delete(_path); } catch { }
            _path = null;
        }
    }

    /// <summary>
    /// Trails left by processes that are gone, each taken (deleted) so only one
    /// starting Grepdesk reports it. Sessions from before the last restart are
    /// dropped silently: shutting Windows down doesn't let processes exit normally.
    /// </summary>
    public static List<Crash> TakeCrashes(string? directory = null, Func<int, DateTime, bool>? isRunning = null, DateTime? bootUtc = null)
    {
        directory ??= ErrorLog.Directory;
        isRunning ??= IsRunning;
        bootUtc ??= DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

        var crashes = new List<Crash>();
        string[] files;
        try { files = System.IO.Directory.GetFiles(directory, Pattern); }
        catch { return crashes; }

        foreach (var file in files)
        {
            if (string.Equals(file, _path, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var lines = File.ReadAllLines(file);
                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (!TryParseHeader(lines, out var pid, out var started))
                {
                    File.Delete(file);
                    continue;
                }
                if (isRunning(pid, started)) continue;

                // Claim it: a Grepdesk starting at the same moment must not report it too.
                var claimed = file + ".taken";
                File.Move(file, claimed);
                File.Delete(claimed);
                if (started > bootUtc) crashes.Add(new Crash(pid, started, lastWrite, lines[1..]));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Taken by another process, or unreadable: leave it.
            }
        }
        return crashes;
    }

    private static bool TryParseHeader(string[] lines, out int pid, out DateTime startedUtc)
    {
        pid = 0;
        startedUtc = default;
        var parts = lines.Length > 0 ? lines[0].Split(' ') : [];
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
            && DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out startedUtc);
    }

    /// <summary>The same process, not a new one that was given the same id.</summary>
    private static bool IsRunning(int pid, DateTime startedUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return Math.Abs((process.StartTime.ToUniversalTime() - startedUtc).TotalSeconds) < 1;
        }
        catch
        {
            return false; // gone
        }
    }

    // ---------------------------------------------------------------------
    // Windows' record of the crash
    // ---------------------------------------------------------------------

    /// <summary>
    /// Windows logs every crashed process in the Application event log:
    /// "Application Error" (1000) names the module that faulted and the
    /// exception code; ".NET Runtime" (1026) has the managed stack, if any.
    /// </summary>
    public static Crash WithSystemReport(Crash crash)
    {
        if (!OperatingSystem.IsWindows()) return crash;
        try
        {
            var since = crash.StartedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var xml = RunWevtutil(
                "qe", "Application", "/f:xml", "/c:50",
                $"/q:*[System[(EventID=1000 or EventID=1026) and TimeCreated[@SystemTime>='{since}']]]");
            return xml is null ? crash : crash with { SystemReport = ParseEvents(xml, crash.ProcessId) };
        }
        catch
        {
            return crash;
        }
    }

    internal static string? ParseEvents(string xml, int pid)
    {
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "Grepdesk.UI.exe";
        var hexPid = $"0x{pid:x}";
        var report = new StringBuilder();

        foreach (var ev in XElement.Parse($"<events>{xml}</events>").Elements(ns + "Event"))
        {
            var id = (string?)ev.Element(ns + "System")?.Element(ns + "EventID");
            var data = ev.Element(ns + "EventData")?.Elements(ns + "Data").ToList() ?? [];
            string? Field(string name) => (string?)data.FirstOrDefault(d => (string?)d.Attribute("Name") == name);

            if (id == "1000" && string.Equals(Field("ProcessId"), hexPid, StringComparison.OrdinalIgnoreCase))
            {
                var code = Field("ExceptionCode") ?? "";
                report.AppendLine($"Windows: {Field("AppName")} crashed in {Field("ModuleName")} {Field("ModuleVersion")}")
                      .AppendLine($"  exception 0x{code} ({ExceptionName(code)}), offset 0x{Field("FaultingOffset")}")
                      .AppendLine($"  module: {Field("ModulePath")}");
            }
            else if (id == "1026" && data.FirstOrDefault()?.Value is { } text
                     && text.Contains(exeName, StringComparison.OrdinalIgnoreCase) && report.Length == 0)
            {
                // .NET's entry has no process id: only taken while nothing better was found.
                report.AppendLine(".NET runtime:").AppendLine(text.Trim());
            }
        }
        return report.Length > 0 ? report.ToString() : null;
    }

    private static string ExceptionName(string code) => code.ToLowerInvariant() switch
    {
        "c0000005" => "access violation: native code used invalid memory",
        "c0000409" => "fail fast / stack buffer overrun",
        "c00000fd" => "stack overflow",
        "c0000374" => "heap corruption",
        "c0000017" or "e06d7363" => "out of memory or a C++ exception",
        "e0434352" => ".NET exception",
        "80000003" => "breakpoint",
        _ => "unknown",
    };

    /// <summary>wevtutil writes in the system's ANSI code page, not UTF-8.</summary>
    private static string? RunWevtutil(params string[] arguments)
    {
        var start = new ProcessStartInfo("wevtutil.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(); } catch { }
            return null;
        }
        return output.Result;
    }
}
