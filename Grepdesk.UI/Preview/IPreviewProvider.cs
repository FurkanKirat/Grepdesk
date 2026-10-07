using Avalonia.Media.Imaging;
using Grepdesk.Core;
using Markdig.Syntax;

namespace Grepdesk.UI.Preview;

/// <summary>
/// Produces the body of the preview panel for one kind of item. To preview a
/// new format, implement this and add it to <see cref="PreviewProviders"/> —
/// the panel itself doesn't change. Providers run on a worker thread.
/// </summary>
public interface IPreviewProvider
{
    bool CanPreview(SearchResult item);

    Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct);
}

/// <summary>
/// What the panel shows: an image, text (optionally monospaced, with a footer
/// such as "only the first pages are shown"), rendered Markdown, or just a message.
/// </summary>
public sealed record PreviewContent
{
    public Bitmap? Image { get; init; }
    public string? Text { get; init; }
    public bool Monospace { get; init; }
    public string? Footer { get; init; }
    public string? Message { get; init; }
    public MarkdownPreview? Markdown { get; init; }

    /// <summary>Extra metadata rows, e.g. ("Pages", "12") or ("Dimensions", "1920 × 1080").</summary>
    public IReadOnlyList<(string Label, string Value)> Details { get; init; } = [];

    public static PreviewContent FromMessage(string message) => new() { Message = message };
}

/// <summary>
/// A parsed Markdown file. Controls can only be built on the UI thread, so the
/// provider parses and decodes the local images, and the panel lays them out.
/// </summary>
/// <param name="Images">Decoded local images, keyed by the URL as written in the file.</param>
public sealed record MarkdownPreview(MarkdownDocument Document, string BaseDirectory, IReadOnlyDictionary<string, Bitmap> Images);

public static class PreviewProviders
{
    // First match wins, so specific formats come before the generic text fallback.
    private static readonly IPreviewProvider[] Providers =
    [
        new FolderPreviewProvider(),
        new ImagePreviewProvider(),
        new PdfPreviewProvider(),
        new DocumentPreviewProvider(),
        new MarkdownPreviewProvider(),
        new ShellPreviewProvider(),
        new TextPreviewProvider(),
    ];

    public static IPreviewProvider? For(SearchResult item) =>
        Providers.FirstOrDefault(p => p.CanPreview(item));
}
