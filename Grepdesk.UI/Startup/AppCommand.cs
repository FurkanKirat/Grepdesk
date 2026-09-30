using System.Text.Json;

namespace Grepdesk.UI.Startup;

public enum CommandKind
{
    /// <summary>The search window, optionally rooted at a folder (no flag, or just a path).</summary>
    OpenSearch,
    ExtractHere,
    ExtractTo,
    Compress,
    Paste
}

/// <summary>
/// What the process was launched to do. Explorer's context-menu verbs map to
/// these flags (see WindowsShellIntegration):
/// <c>Grepdesk.UI.exe [--extract-here|--extract-to|--compress|--paste] [--benchmark] paths...</c>
/// </summary>
public sealed record AppCommand(CommandKind Kind, IReadOnlyList<string> Paths, bool Benchmark = false)
{
    /// <summary>Jobs run in a progress window and go through the single-instance host.</summary>
    public bool IsJob => Kind != CommandKind.OpenSearch;

    public static AppCommand Parse(IReadOnlyList<string> args)
    {
        var kind = CommandKind.OpenSearch;
        var benchmark = false;
        var paths = new List<string>();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--extract-here": kind = CommandKind.ExtractHere; break;
                case "--extract-to": kind = CommandKind.ExtractTo; break;
                case "--compress": kind = CommandKind.Compress; break;
                case "--paste": kind = CommandKind.Paste; break;
                case "--benchmark": benchmark = true; break;
                default:
                    if (CleanPath(arg) is { } path)
                        paths.Add(path);
                    break;
            }
        }

        return new AppCommand(kind, paths, benchmark);
    }

    /// <summary>
    /// Explorer passes a drive root as <c>"D:\"</c>, and Windows' command-line
    /// rules read <c>\"</c> as an escaped quote, so the argument arrives as
    /// <c>D:"</c>. A quote can never be part of a Windows path, so it's dropped;
    /// the bare <c>D:</c> left over must become <c>D:\</c>, because <c>D:</c>
    /// alone means "the current folder on drive D".
    /// </summary>
    internal static string? CleanPath(string arg)
    {
        var path = arg.Trim().TrimEnd('"');
        if (path.Length == 0)
            return null;
        if (path.Length == 2 && path[1] == ':')
            path += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path);
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static AppCommand? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AppCommand>(json); }
        catch (JsonException) { return null; }
    }
}
