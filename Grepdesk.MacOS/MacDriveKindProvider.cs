using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Grepdesk.Core;

namespace Grepdesk.MacOS;

/// <summary>
/// Finds the volume behind a path with <c>df</c>, then asks
/// <c>diskutil info -plist</c> whether it is solid state or removable.
/// Anything unexpected yields <see cref="DriveKind.Unknown"/>, which picks a
/// middle-of-the-road worker count — never an error.
/// </summary>
public class MacDriveKindProvider : IDriveKindProvider
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, DriveKind> _cache = new(StringComparer.Ordinal);

    public DriveKind GetKind(string path)
    {
        var mount = MountOf(Path.GetFullPath(path));
        if (mount is null)
            return DriveKind.Unknown;
        return _cache.GetOrAdd(mount.Value.MountPoint, _ => Classify(mount.Value.Source, mount.Value.MountPoint));
    }

    /// <summary>
    /// <c>df -P</c> output, second line: source, 4 number columns, then the
    /// mount point (which may contain spaces, so it's everything after the 5th field).
    /// </summary>
    public static (string Source, string MountPoint)? ParseDf(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
        if (line is null)
            return null;
        var match = Regex.Match(line, @"^(\S+)\s+\d+\s+\d+\s+\d+\s+\d+%\s+(.+)$");
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value.Trim()) : null;
    }

    /// <summary>Reads a boolean key (<c>&lt;key&gt;SolidState&lt;/key&gt;&lt;true/&gt;</c>) from diskutil's plist.</summary>
    public static bool? PlistBool(string plist, string key)
    {
        var match = Regex.Match(plist, $@"<key>{Regex.Escape(key)}</key>\s*<(true|false)\s*/>");
        return match.Success ? match.Groups[1].Value == "true" : null;
    }

    private static (string Source, string MountPoint)? MountOf(string path) =>
        Run("/bin/df", ["-P", path], out var output) ? ParseDf(output) : null;

    private static DriveKind Classify(string source, string mountPoint)
    {
        // SMB/AFP shares show up as //user@host/share, NFS as host:/path.
        if (source.StartsWith("//", StringComparison.Ordinal) || (source.Contains(':') && !source.StartsWith("/dev/", StringComparison.Ordinal)))
            return DriveKind.Network;

        if (!Run("/usr/sbin/diskutil", ["info", "-plist", mountPoint], out var plist))
            return DriveKind.Unknown;

        if (PlistBool(plist, "Internal") == false && (PlistBool(plist, "RemovableMedia") == true || PlistBool(plist, "Ejectable") == true)
            && PlistBool(plist, "SolidState") != true)
            return DriveKind.Removable;

        return PlistBool(plist, "SolidState") switch
        {
            true => DriveKind.SolidState,
            false => DriveKind.Rotational,
            null => DriveKind.Unknown
        };
    }

    private static bool Run(string exe, string[] arguments, out string output)
    {
        output = "";
        if (!File.Exists(exe))
            return false;

        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill();
                return false;
            }
            output = read.Result;
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
