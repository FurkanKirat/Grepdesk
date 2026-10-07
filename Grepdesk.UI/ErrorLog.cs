using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Grepdesk.UI;

/// <summary>
/// Unexpected errors, written to a daily log file instead of vanishing with
/// the process: %LOCALAPPDATA%\Grepdesk\logs on Windows, ~/.local/share/Grepdesk/logs
/// elsewhere. Only the newest files are kept. Never throws: a failing log
/// must not turn a handled error into a crash.
/// </summary>
internal static class ErrorLog
{
    private const int KeepFiles = 10;
    private static readonly Lock Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grepdesk", "logs");

    /// <param name="source">Where it was caught, e.g. "UI thread" or "unobserved task".</param>
    /// <returns>The log file written to, or null if even that failed.</returns>
    public static string? Write(Exception exception, string source, string? directory = null)
    {
        directory ??= Directory;
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"grepdesk-{DateTime.Now:yyyy-MM-dd}.log");

                var entry = new StringBuilder()
                    .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}")
                    .AppendLine($"Grepdesk {Version} · {RuntimeInformation.OSDescription} · .NET {Environment.Version}")
                    .AppendLine($"Command line: {Environment.CommandLine}")
                    .AppendLine(exception.ToString())
                    .AppendLine();
                File.AppendAllText(path, entry.ToString());

                DeleteOldFiles(directory);
                return path;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteOldFiles(string directory)
    {
        var old = new DirectoryInfo(directory).GetFiles("grepdesk-*.log")
            .OrderByDescending(f => f.Name)
            .Skip(KeepFiles);
        foreach (var file in old)
            file.Delete();
    }

    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "?";
}
