using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grepdesk.Core.Grouping;

public enum SkipReason
{
    /// <summary>The target folder already holds an item with this name; nothing is overwritten.</summary>
    NameTaken,
    /// <summary>A program has it open, or access was denied.</summary>
    InUse,
    /// <summary>The item is the target folder itself (a folder of the group named like the group).</summary>
    IsTarget,
}

/// <summary>What a name typed for the target folder would do.</summary>
public enum TargetState { New, ExistingFolder, ExistingFile, Invalid }

/// <param name="Moved">Paths that were moved (their old location).</param>
/// <param name="Skipped">Items left in place, with why.</param>
/// <param name="FolderExisted">The items went into a folder that was already there.</param>
public sealed record OrganizeResult(
    string TargetFolder,
    IReadOnlyList<string> Moved,
    IReadOnlyList<(string Path, SkipReason Reason)> Skipped,
    bool FolderExisted = false);

/// <summary>Tidies a group into a subfolder instead of deleting it.</summary>
public static class FolderOrganizer
{
    /// <summary>
    /// A folder name from a group label. For a series pattern ("dracula_idle_*.png")
    /// the extension and the wildcard go ("dracula_idle"); other labels ("itch.io")
    /// are only cleaned of characters Windows rejects.
    /// </summary>
    public static string SuggestName(string label, bool isPattern)
    {
        var name = label;
        if (isPattern)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 1) name = name[..^ext.Length];
            name = name.Replace('*', ' ');
        }
        return Sanitize(name);
    }

    /// <summary>Removes characters not allowed in a folder name and tidies the spacing.</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['*', '?', '<', '>', '|', ':', '"', '/', '\\']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Trim(' ', '.', '_', '-');
    }

    public static TargetState CheckTarget(string parent, string name)
    {
        var cleaned = Sanitize(name);
        if (cleaned.Length == 0) return TargetState.Invalid;
        var path = Path.Combine(parent, cleaned);
        return Directory.Exists(path) ? TargetState.ExistingFolder
            : File.Exists(path) ? TargetState.ExistingFile
            : TargetState.New;
    }

    /// <summary>
    /// <paramref name="name"/>, or "name 2", "name 3"... if something by that name
    /// already exists, so a suggested folder never silently lands in another one.
    /// </summary>
    public static string UniqueName(string parent, string name)
    {
        if (name.Length == 0 || CheckTarget(parent, name) == TargetState.New) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (CheckTarget(parent, candidate) == TargetState.New) return candidate;
        }
    }

    /// <summary>
    /// Moves the items into <paramref name="subfolderName"/> under <paramref name="parent"/>
    /// (created if needed). Never overwrites: an item whose name is already taken in the
    /// target stays where it is and is reported as skipped.
    /// </summary>
    public static OrganizeResult MoveInto(string parent, string subfolderName, IEnumerable<string> items)
    {
        var name = Sanitize(subfolderName);
        if (name.Length == 0) throw new ArgumentException("The folder name is empty after removing invalid characters.", nameof(subfolderName));

        var target = Path.Combine(parent, name);
        if (File.Exists(target)) throw new IOException($"A file named \"{name}\" already exists.");
        var existed = Directory.Exists(target);
        Directory.CreateDirectory(target);

        var moved = new List<string>();
        var skipped = new List<(string, SkipReason)>();
        foreach (var source in items)
        {
            // The target folder itself may be one of the selected items (a folder in the group).
            if (string.Equals(Path.TrimEndingDirectorySeparator(source), target, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add((source, SkipReason.IsTarget));
                continue;
            }

            var destination = Path.Combine(target, Path.GetFileName(source));
            try
            {
                if (File.Exists(destination) || Directory.Exists(destination)) { skipped.Add((source, SkipReason.NameTaken)); continue; }

                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
                moved.Add(source);
            }
            catch (IOException) { skipped.Add((source, SkipReason.InUse)); }
            catch (UnauthorizedAccessException) { skipped.Add((source, SkipReason.InUse)); }
        }

        return new OrganizeResult(target, moved, skipped, existed);
    }

    /// <summary>
    /// Puts back what <see cref="MoveInto"/> moved. Something that reappeared at
    /// the old location in the meantime is left alone. The subfolder is removed
    /// if this left it empty.
    /// </summary>
    /// <returns>How many items went back.</returns>
    public static int Undo(OrganizeResult result)
    {
        var restored = 0;
        foreach (var original in result.Moved)
        {
            var current = Path.Combine(result.TargetFolder, Path.GetFileName(original));
            try
            {
                if (File.Exists(original) || Directory.Exists(original)) continue;
                if (Directory.Exists(current)) Directory.Move(current, original);
                else if (File.Exists(current)) File.Move(current, original);
                else continue;
                restored++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        try
        {
            if (Directory.Exists(result.TargetFolder) && !Directory.EnumerateFileSystemEntries(result.TargetFolder).Any())
                Directory.Delete(result.TargetFolder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return restored;
    }
}
