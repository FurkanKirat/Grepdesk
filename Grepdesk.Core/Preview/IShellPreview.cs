namespace Grepdesk.Core.Preview;

/// <summary>A thumbnail as 32-bit BGRA pixels, top row first.</summary>
public sealed record ShellThumbnail(int Width, int Height, byte[] Pixels, bool HasAlpha);

/// <summary>What the OS knows about a media or document file. Anything it doesn't know is null.</summary>
public sealed record ShellMediaInfo
{
    public TimeSpan? Duration { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    /// <summary>Bits per second.</summary>
    public long? Bitrate { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public int? Slides { get; init; }
}

/// <summary>
/// The file manager's own thumbnails and metadata: video frames, album art,
/// the first slide of a presentation — whatever the installed handlers provide.
/// </summary>
public interface IShellPreview
{
    /// <summary>A real thumbnail no larger than <paramref name="size"/> pixels, or null (no generic icons).</summary>
    ShellThumbnail? GetThumbnail(string path, int size);

    ShellMediaInfo GetMediaInfo(string path);
}
