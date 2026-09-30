namespace Grepdesk.Core.Jobs;

public enum ConflictChoice
{
    Overwrite,
    Skip,
    KeepBoth
}

/// <summary>A file that is about to be written already exists at the destination.</summary>
public sealed record ConflictInfo(
    string SourceName,
    long SourceSize,
    DateTime SourceLastWrite,
    string DestinationPath,
    long DestinationSize,
    DateTime DestinationLastWrite);

public interface IConflictResolver
{
    /// <summary>
    /// Asks what to do with a conflict. May be called from several worker
    /// threads at once; implementations serialize the questions themselves.
    /// </summary>
    Task<ConflictChoice> ResolveAsync(ConflictInfo conflict, CancellationToken ct);
}

public static class ConflictPolicy
{
    // Zip stores timestamps in DOS format with 2-second resolution, and FAT
    // volumes do the same, so an exact comparison would report false changes.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Same size and timestamp: overwriting would change nothing, so the
    /// engines skip these without asking. Only a conflict that could lose
    /// data is put to the user.
    /// </summary>
    public static bool LooksIdentical(ConflictInfo c) =>
        c.SourceSize == c.DestinationSize &&
        (c.SourceLastWrite - c.DestinationLastWrite).Duration() <= TimestampTolerance;

    /// <summary>"report.pdf" → "report (2).pdf", "report (3).pdf", … — the first free name.</summary>
    public static string NextFreePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
