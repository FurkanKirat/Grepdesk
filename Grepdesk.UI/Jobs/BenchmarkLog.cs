using Grepdesk.Core.Jobs;

namespace Grepdesk.UI.Jobs;

/// <summary>
/// Output for <c>--benchmark</c> runs: one tab-separated line per job,
/// appended to %LOCALAPPDATA%\Grepdesk\benchmark.log and written to stdout
/// (visible when the exe is run from a shell with output redirected).
/// </summary>
internal static class BenchmarkLog
{
    public static void Write(JobSpec spec, TimeSpan elapsed, ProgressSnapshot snap, int errors)
    {
        var line = string.Join('\t',
            DateTime.Now.ToString("s"),
            spec.Kind,
            elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            snap.DoneFiles,
            snap.DoneBytes,
            errors,
            spec.Title);

        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grepdesk");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "benchmark.log"), line + Environment.NewLine);
        }
        catch (IOException) { }

        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}
