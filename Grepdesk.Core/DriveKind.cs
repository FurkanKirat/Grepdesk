namespace Grepdesk.Core;

public enum DriveKind
{
    Unknown,
    SolidState,
    Rotational,
    Removable,
    Network
}

public interface IDriveKindProvider
{
    DriveKind GetKind(string path);
}

/// <summary>Fallback for platforms without a native probe: only network paths are recognised.</summary>
public sealed class BasicDriveKindProvider : IDriveKindProvider
{
    public DriveKind GetKind(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return DriveKind.Unknown;
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return DriveKind.Network;

            return new DriveInfo(root).DriveType switch
            {
                DriveType.Network => DriveKind.Network,
                DriveType.Removable => DriveKind.Removable,
                _ => DriveKind.Unknown
            };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return DriveKind.Unknown;
        }
    }
}

/// <summary>
/// How many files to process at once. Parallel I/O pays off on SSDs and
/// especially on network shares (it hides latency), but on a spinning disk
/// or a USB stick it makes the head/controller thrash and is slower than
/// going one file at a time.
/// </summary>
public static class WorkerPolicy
{
    /// <summary>
    /// Copy, move and extract: mostly waiting on the file system (create,
    /// write, close — plus antivirus filters) rather than the CPU, so more
    /// workers than cores still helps. Measured on NVMe, 10,000 small files:
    /// 8 workers 4.2–4.5 s, 16 → 3.3 s, 32 → 2.5–2.8 s, 64 no better.
    /// </summary>
    public static int FileOperations(params DriveKind[] drives)
    {
        if (drives.Any(d => d is DriveKind.Rotational or DriveKind.Removable))
            return 1;
        if (drives.Any(d => d == DriveKind.Network))
            return 16;
        if (drives.All(d => d == DriveKind.SolidState))
            return 32;
        return 4;
    }

    /// <summary>Compression: CPU-bound, so capped at the core count.</summary>
    public static int Compression(params DriveKind[] drives)
    {
        if (drives.Any(d => d is DriveKind.Rotational or DriveKind.Removable))
            return 1;
        return drives.All(d => d == DriveKind.SolidState)
            ? Environment.ProcessorCount
            : Math.Min(4, Environment.ProcessorCount);
    }
}
