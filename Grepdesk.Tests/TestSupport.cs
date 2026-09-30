using Grepdesk.Core.Jobs;

namespace Grepdesk.Tests;

/// <summary>A temp folder that deletes itself.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir() => Path = Directory.CreateTempSubdirectory("grepdesk-test-").FullName;

    /// <summary>A throwaway folder under <paramref name="root"/> (e.g. another drive).</summary>
    public TempDir(string root) =>
        Path = Directory.CreateDirectory(System.IO.Path.Combine(root, $".grepdesk-test-{Guid.NewGuid():N}")).FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content, DateTime? lastWrite = null)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (lastWrite is { } t)
            File.SetLastWriteTime(full, t);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// A temp folder on a different drive than the system temp folder, for
/// cross-volume copy/move tests. Null when the machine has only one fixed drive
/// (those tests then return early).
/// </summary>
public static class OtherDrive
{
    public static TempDir? CreateTempDir()
    {
        var tempRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath());
        var other = DriveInfo.GetDrives().FirstOrDefault(d =>
            d.DriveType == DriveType.Fixed && d.IsReady &&
            !string.Equals(d.RootDirectory.FullName, tempRoot, StringComparison.OrdinalIgnoreCase));
        return other is null ? null : new TempDir(other.RootDirectory.FullName);
    }
}

/// <summary>Answers every conflict the same way and counts how often it was asked.</summary>
public sealed class FixedResolver(ConflictChoice choice) : IConflictResolver
{
    private int _asked;

    public int Asked => _asked;

    public Task<ConflictChoice> ResolveAsync(ConflictInfo conflict, CancellationToken ct)
    {
        Interlocked.Increment(ref _asked);
        return Task.FromResult(choice);
    }
}

/// <summary>A fact that only runs on Windows (skipped elsewhere, e.g. in the Linux container).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
    }
}
