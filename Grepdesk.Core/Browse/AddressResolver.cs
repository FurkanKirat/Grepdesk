namespace Grepdesk.Core.Browse;

public enum AddressKind
{
    Folder,
    File,
    /// <summary>A place only the OS file manager can show (<c>shell:startup</c>, <c>::{CLSID}</c>): handed to it as typed.</summary>
    Shell,
    NotFound
}

public sealed record AddressTarget(AddressKind Kind, string Path);

/// <summary>One clickable part of the breadcrumb bar.</summary>
public sealed record AddressSegment(string Label, string Path);

/// <summary>Turns what was typed in the address bar into a place, and a place into breadcrumbs.</summary>
public static class AddressResolver
{
    public static AddressTarget Resolve(string input, string? currentFolder)
    {
        var text = input.Trim().Trim('"').Trim();
        if (text.Length == 0) return new AddressTarget(AddressKind.NotFound, input);

        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("::", StringComparison.Ordinal))
            return new AddressTarget(AddressKind.Shell, text);

        text = Environment.ExpandEnvironmentVariables(text);
        if (!OperatingSystem.IsWindows() && (text == "~" || text.StartsWith("~/", StringComparison.Ordinal)))
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];

        // "D:" alone means "the current folder on D:", which is never what is meant in an address bar.
        if (OperatingSystem.IsWindows() && text.Length == 2 && text[1] == ':')
            text += Path.DirectorySeparatorChar;

        try
        {
            if (!Path.IsPathRooted(text))
            {
                if (currentFolder is null) return new AddressTarget(AddressKind.NotFound, input);
                text = Path.Combine(currentFolder, text);
            }
            var full = Path.GetFullPath(text);
            if (Directory.Exists(full)) return new AddressTarget(AddressKind.Folder, PathUtil.Normalize(full));
            if (File.Exists(full)) return new AddressTarget(AddressKind.File, full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { }

        return new AddressTarget(AddressKind.NotFound, input);
    }

    /// <summary>
    /// The breadcrumbs of a folder: the root first ("C:", "\\server\share", "/"),
    /// then every folder down to it.
    /// </summary>
    public static IReadOnlyList<AddressSegment> Segments(string folder)
    {
        var full = PathUtil.Normalize(folder);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return [new AddressSegment(full, full)];
        var rootLength = root.Length;
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar; // a share root comes back as "\\server\share"

        // TrimEndingDirectorySeparator keeps a root's separator; the label shouldn't have it ("C:", not "C:\").
        var rootLabel = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = new List<AddressSegment> { new(rootLabel.Length == 0 ? root : rootLabel, root) };

        var current = root;
        foreach (var part in full[rootLength..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add(new AddressSegment(part, current));
        }
        return segments;
    }
}
