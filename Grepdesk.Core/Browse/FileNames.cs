namespace Grepdesk.Core.Browse;

public enum NameProblem { None, Empty, InvalidCharacters, Reserved, EndsWithDotOrSpace }

public enum RenameOutcome { Renamed, Unchanged, InvalidName, AlreadyExists, NotFound, Failed }

public sealed record RenameResult(RenameOutcome Outcome, string Path, NameProblem Problem = NameProblem.None, Exception? Error = null)
{
    public bool IsSuccess => Outcome is RenameOutcome.Renamed or RenameOutcome.Unchanged;
}

/// <summary>Naming rules for new and renamed files and folders, and the rename itself.</summary>
public static class FileNames
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    // Windows refuses these as names, with or without an extension ("con.txt" too).
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static NameProblem Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return NameProblem.Empty;
        if (name.IndexOfAny(InvalidChars) >= 0) return NameProblem.InvalidCharacters;
        if (!OperatingSystem.IsWindows()) return NameProblem.None;

        // Windows silently drops a trailing dot or space, so "a." would become "a".
        if (name.EndsWith('.') || name.EndsWith(' ')) return NameProblem.EndsWithDotOrSpace;
        var stem = name.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(stem) ? NameProblem.Reserved : NameProblem.None;
    }

    /// <summary>
    /// <paramref name="name"/> if nothing in <paramref name="folder"/> has it yet,
    /// otherwise the first free "name (2).ext", "name (3).ext"…, as Explorer does.
    /// </summary>
    public static string UniqueName(string folder, string name, bool isDirectory)
    {
        if (!Exists(Path.Combine(folder, name))) return name;

        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? "" : Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (!Exists(Path.Combine(folder, candidate))) return candidate;
        }
    }

    /// <summary>
    /// Renames a file or folder in place. A change in case only ("readme" →
    /// "README") is a real rename on Windows, so it is allowed even though the
    /// new name "already exists".
    /// </summary>
    public static RenameResult Rename(string path, string newName)
    {
        newName = newName.Trim();
        var problem = Validate(newName);
        if (problem != NameProblem.None) return new RenameResult(RenameOutcome.InvalidName, path, problem);

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path)) return new RenameResult(RenameOutcome.NotFound, path);

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!;
        var target = Path.Combine(parent, newName);
        var oldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return new RenameResult(RenameOutcome.Unchanged, path);

        var caseOnly = string.Equals(oldName, newName, PathUtil.Comparison);
        if (!caseOnly && Exists(target)) return new RenameResult(RenameOutcome.AlreadyExists, path);

        try
        {
            if (isDirectory)
            {
                if (caseOnly)
                {
                    // Directory.Move refuses source == destination when they differ only by case.
                    var step = Path.Combine(parent, $"{newName}.{Guid.NewGuid():N}.tmp");
                    Directory.Move(path, step);
                    Directory.Move(step, target);
                }
                else
                {
                    Directory.Move(path, target);
                }
            }
            else
            {
                File.Move(path, target);
            }
            return new RenameResult(RenameOutcome.Renamed, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RenameResult(RenameOutcome.Failed, path, Error: ex);
        }
    }

    /// <summary>Creates a new folder with a free name based on <paramref name="name"/> and returns its path.</summary>
    public static string CreateFolder(string parent, string name)
    {
        var path = Path.Combine(parent, UniqueName(parent, name, isDirectory: true));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
