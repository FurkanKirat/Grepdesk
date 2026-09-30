using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Grepdesk.Core;

namespace Grepdesk.Linux;

/// <summary>
/// Finds the block device behind a path via /proc/self/mountinfo and asks
/// sysfs whether it spins (queue/rotational) or is removable — the same
/// flags lsblk shows. Network file systems are recognised by type.
/// </summary>
public class LinuxDriveKindProvider : IDriveKindProvider
{
    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.Ordinal)
    {
        "nfs", "nfs4", "cifs", "smb3", "smbfs", "fuse.sshfs", "fuse.rclone", "ceph", "glusterfs", "davfs", "fuse.davfs2"
    };

    private readonly ConcurrentDictionary<string, DriveKind> _cache = new(StringComparer.Ordinal);
    private readonly Func<string> _readMountInfo;
    private readonly string _sysRoot;

    public LinuxDriveKindProvider() : this(() => File.ReadAllText("/proc/self/mountinfo"), "/sys") { }

    /// <summary>For tests: fake mountinfo text and sysfs folder.</summary>
    public LinuxDriveKindProvider(Func<string> readMountInfo, string sysRoot)
    {
        _readMountInfo = readMountInfo;
        _sysRoot = sysRoot;
    }

    public DriveKind GetKind(string path)
    {
        try
        {
            var mount = FindMount(ParseMountInfo(_readMountInfo()), Path.GetFullPath(path));
            return mount is null ? DriveKind.Unknown : _cache.GetOrAdd(mount.MountPoint, _ => Classify(mount));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return DriveKind.Unknown;
        }
    }

    public sealed record Mount(string MountPoint, string DeviceNumber, string FileSystem, string Source);

    /// <summary>
    /// mountinfo line: <c>36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw</c>
    /// (see proc(5)); fields after the " - " separator are type, source, options.
    /// </summary>
    public static List<Mount> ParseMountInfo(string text)
    {
        var mounts = new List<Mount>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
                continue;
            var left = line[..separator].Split(' ');
            var right = line[(separator + 3)..].Split(' ');
            if (left.Length < 5 || right.Length < 2)
                continue;
            mounts.Add(new Mount(Unescape(left[4]), left[2], right[0], Unescape(right[1])));
        }
        return mounts;
    }

    /// <summary>The mount with the longest mount point that contains the path.</summary>
    public static Mount? FindMount(IEnumerable<Mount> mounts, string path) =>
        mounts.Where(m => m.MountPoint == "/" || path == m.MountPoint || path.StartsWith(m.MountPoint + "/", StringComparison.Ordinal))
              .OrderByDescending(m => m.MountPoint.Length)
              .FirstOrDefault();

    private DriveKind Classify(Mount mount)
    {
        if (NetworkFileSystems.Contains(mount.FileSystem))
            return DriveKind.Network;

        // Btrfs and some others report an anonymous device number (0:NN);
        // fall back to the device named as the mount source.
        var device = ResolveDevice(Path.Combine(_sysRoot, "dev", "block", mount.DeviceNumber));
        if (device is null && mount.Source.StartsWith("/dev/", StringComparison.Ordinal))
            device = ResolveDevice(Path.Combine(_sysRoot, "class", "block", Path.GetFileName(mount.Source)));
        if (device is null)
            return DriveKind.Unknown;

        // Flags live on the whole disk, not on a partition of it.
        var disk = File.Exists(Path.Combine(device, "partition")) ? Path.GetDirectoryName(device)! : device;

        if (ReadFlag(Path.Combine(disk, "removable")) == "1")
            return DriveKind.Removable;
        return ReadFlag(Path.Combine(disk, "queue", "rotational")) switch
        {
            "1" => DriveKind.Rotational,
            "0" => DriveKind.SolidState,
            _ => DriveKind.Unknown
        };
    }

    private static string? ResolveDevice(string link)
    {
        if (!Directory.Exists(link))
            return null;
        var target = new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true);
        return (target?.FullName ?? link).TrimEnd('/');
    }

    private static string? ReadFlag(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    // mountinfo escapes space, tab, newline and backslash as \040-style octal.
    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length &&
                IsOctal(value[i + 1]) && IsOctal(value[i + 2]) && IsOctal(value[i + 3]))
            {
                sb.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();

        static bool IsOctal(char c) => c is >= '0' and <= '7';
    }
}
