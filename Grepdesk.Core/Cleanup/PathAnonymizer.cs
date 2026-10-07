using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grepdesk.Core.Cleanup;

/// <summary>
/// Rewrites paths so they can be pasted into an AI chat without giving away
/// who the user is or what their files are called, while keeping what matters
/// for "is this safe to delete?": which program or system folder it is.
///
/// - The profile folder becomes %USERPROFILE% (AppData parts %LOCALAPPDATA% / %APPDATA%),
///   and the user name is replaced anywhere else it appears.
/// - Inside personal folders (Desktop, Documents, Downloads, Pictures, Videos,
///   Music, OneDrive) names are replaced: folders by &lt;folder&gt;, files by
///   &lt;file&gt; plus the extension, which is all a safety question needs.
/// - Everything else (Program Files, AppData\Local\NVIDIA\DXCache, node_modules...)
///   is kept: those names identify software, not the person.
/// </summary>
public sealed class PathAnonymizer
{
    private static readonly string[] PersonalFolders =
        ["Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "OneDrive"];

    private readonly (string Prefix, string Replacement)[] _prefixes;
    private readonly string _userName;
    private readonly char _sep = Path.DirectorySeparatorChar;

    public PathAnonymizer(string userProfile, string localAppData, string roamingAppData)
    {
        _userName = Path.GetFileName(Path.TrimEndingDirectorySeparator(userProfile));

        // Longest first, so %LOCALAPPDATA% wins over %USERPROFILE%.
        _prefixes = new[]
            {
                (Trim(localAppData), "%LOCALAPPDATA%"),
                (Trim(roamingAppData), "%APPDATA%"),
                (Trim(userProfile), "%USERPROFILE%"),
            }
            .Where(p => p.Item1.Length > 0)
            .OrderByDescending(p => p.Item1.Length)
            .ToArray();
    }

    public static PathAnonymizer ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    public string Anonymize(string path)
    {
        var result = path;
        foreach (var (prefix, replacement) in _prefixes)
        {
            if (result.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return replacement;
            if (result.StartsWith(prefix + _sep, StringComparison.OrdinalIgnoreCase))
            {
                result = replacement + result[prefix.Length..];
                break;
            }
        }

        result = HidePersonalNames(result);

        // The user name can still show up elsewhere (D:\Users\name\..., a folder named after them).
        if (_userName.Length >= 2)
            result = result.Replace(_userName, "<user>", StringComparison.OrdinalIgnoreCase);

        return result;
    }

    private string HidePersonalNames(string path)
    {
        var parts = path.Split(_sep);
        var personalFrom = -1;
        for (var i = 0; i < parts.Length; i++)
        {
            if (PersonalFolders.Any(f => parts[i].StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            {
                personalFrom = i;
                break;
            }
        }
        if (personalFrom < 0) return path;

        for (var i = personalFrom + 1; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            var ext = Path.GetExtension(parts[i]);
            parts[i] = isLast && ext.Length is > 1 and <= 6 ? "<file>" + ext : isLast ? "<item>" : "<folder>";
        }
        return string.Join(_sep, parts);
    }

    private static string Trim(string path) => string.IsNullOrEmpty(path) ? "" : Path.TrimEndingDirectorySeparator(path);
}
