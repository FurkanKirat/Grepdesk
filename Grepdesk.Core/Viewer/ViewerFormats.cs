using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grepdesk.Core.ContentSearch;

namespace Grepdesk.Core.Viewer;

/// <summary>How the viewer window shows a file.</summary>
public enum ViewerKind
{
    /// <summary>Plain text and code: the editor.</summary>
    Text,
    /// <summary>Markdown: rendered, or its source in the editor.</summary>
    Markdown,
    /// <summary>Images Avalonia decodes itself.</summary>
    Image,
    /// <summary>Images only the OS can decode (HEIC, TIFF, camera raw...), shown through its thumbnailer.</summary>
    ShellImage,
    Pdf,
    Video,
    Audio,
    /// <summary>Office documents: their extracted text, read-only.</summary>
    Document,
    /// <summary>Anything else: shown as text if it turns out to be text, otherwise a summary card.</summary>
    Other
}

/// <summary>A group of related extensions the settings page lets the user associate together.</summary>
/// <param name="Id">Stable name, also the suffix of its label key ("FileTypes" + Id).</param>
public sealed record FileTypeCategory(string Id, IReadOnlyList<string> Extensions, bool OnByDefault);

/// <summary>
/// Which file types the viewer handles and how. Also the list of extensions
/// Grepdesk registers under Explorer's "Open with".
/// </summary>
public static class ViewerFormats
{
    public static readonly IReadOnlySet<string> MarkdownExtensions = Set(".md", ".markdown", ".mdown", ".mkd");

    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(
        new PlainTextExtractor().SupportedExtensions.Except(MarkdownExtensions).Concat(
        [
            ".rst", ".tex", ".tsv", ".srt", ".vtt", ".nfo", ".cfg", ".conf", ".cmd", ".psm1", ".zsh", ".fish",
            ".mjs", ".cjs", ".jsonc", ".kt", ".kts", ".swift", ".dart", ".lua", ".pl", ".r", ".fs", ".fsx",
            ".vb", ".xaml", ".axaml", ".props", ".targets", ".cmake", ".graphql", ".proto", ".tf", ".hcl",
            ".iss", ".gitignore", ".gitattributes", ".editorconfig", ".dockerignore", ".diff", ".patch",
            ".reg", ".inf", ".cc", ".cxx", ".hh", ".m", ".scala", ".groovy", ".bash", ".psd1", ".vbs",
        ]),
        StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> TextFileNames =
        Set("Dockerfile", "Makefile", "LICENSE", "README", "CHANGELOG", "CODEOWNERS");

    public static readonly IReadOnlySet<string> ImageExtensions = Set(".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp");

    public static readonly IReadOnlySet<string> ShellImageExtensions = Set(
        ".heic", ".heif", ".avif", ".jxl", ".tif", ".tiff", ".ico", ".psd", ".svg",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2");

    public static readonly IReadOnlySet<string> VideoExtensions = Set(
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".m2ts", ".mts", ".3gp");

    public static readonly IReadOnlySet<string> AudioExtensions = Set(
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff");

    public static readonly IReadOnlySet<string> DocumentExtensions = new HashSet<string>(
        ExtractorRegistry.AllSupportedExtensions.Where(e =>
            ExtractorRegistry.TryGetExtractor(e, out var x) && x is not PlainTextExtractor and not PdfExtractor),
        StringComparer.OrdinalIgnoreCase);

    public static ViewerKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (MarkdownExtensions.Contains(ext)) return ViewerKind.Markdown;
        if (TextExtensions.Contains(ext) || TextFileNames.Contains(Path.GetFileName(path))) return ViewerKind.Text;
        if (ImageExtensions.Contains(ext)) return ViewerKind.Image;
        if (ShellImageExtensions.Contains(ext)) return ViewerKind.ShellImage;
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)) return ViewerKind.Pdf;
        if (VideoExtensions.Contains(ext)) return ViewerKind.Video;
        if (AudioExtensions.Contains(ext)) return ViewerKind.Audio;
        if (DocumentExtensions.Contains(ext)) return ViewerKind.Document;
        return ViewerKind.Other;
    }

    /// <summary>
    /// Files whose double-click runs, installs or launches something (scripts,
    /// registry files, solutions, web pages). The viewer still shows them, but
    /// they stay out of "Open with": a new handler makes Windows ask how to open
    /// the type, and a batch file that opens in an editor no longer runs.
    /// </summary>
    public static readonly IReadOnlySet<string> LaunchedExtensions = Set(
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".js", ".reg", ".inf",
        ".sh", ".bash", ".zsh", ".fish", ".htm", ".html", ".sln", ".csproj");

    /// <summary>Source code, split from the rest of <see cref="TextExtensions"/> so the two can be associated apart.</summary>
    private static readonly IReadOnlySet<string> CodeExtensions = Set(
        ".cs", ".py", ".ts", ".jsx", ".tsx", ".mjs", ".cjs", ".cpp", ".c", ".h", ".hpp", ".cc", ".cxx", ".hh",
        ".java", ".go", ".rs", ".rb", ".php", ".css", ".scss", ".less", ".vue", ".svelte", ".sql",
        ".kt", ".kts", ".swift", ".dart", ".lua", ".pl", ".r", ".fs", ".fsx", ".vb", ".m", ".scala", ".groovy",
        ".xaml", ".axaml", ".props", ".targets", ".gradle", ".cmake", ".graphql", ".proto", ".tf", ".hcl", ".iss");

    /// <summary>
    /// Every type Grepdesk can be associated with, grouped as the settings page
    /// shows them. An extension sits in the first group that lists it. Groups
    /// not on by default open in a lesser view (OS thumbnail, extracted text).
    /// </summary>
    public static readonly IReadOnlyList<FileTypeCategory> FileTypeCategories = BuildCategories();

    /// <summary>Extensions offered in Explorer's "Open with" list until the user picks their own.</summary>
    public static IEnumerable<string> OpenWithExtensions =>
        FileTypeCategories.Where(c => c.OnByDefault).SelectMany(c => c.Extensions).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every extension the user may associate with Grepdesk.</summary>
    public static IEnumerable<string> AssociableExtensions =>
        FileTypeCategories.SelectMany(c => c.Extensions).Order(StringComparer.OrdinalIgnoreCase);

    private static List<FileTypeCategory> BuildCategories()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new List<FileTypeCategory>();

        void Add(string id, IEnumerable<string> extensions, bool onByDefault)
        {
            var list = extensions
                .Where(e => e.Length > 1 && !e.StartsWith(".git") && e is not ".editorconfig" and not ".dockerignore" and not ".env")
                .Where(e => !LaunchedExtensions.Contains(e))
                .Where(taken.Add)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (list.Count > 0) categories.Add(new FileTypeCategory(id, list, onByDefault));
        }

        Add("Text", TextExtensions.Where(e => !CodeExtensions.Contains(e)), true);
        Add("Code", TextExtensions.Where(CodeExtensions.Contains), true);
        Add("Markdown", MarkdownExtensions, true);
        Add("Pdf", [".pdf"], true);
        Add("Images", ImageExtensions, true);
        Add("ShellImages", ShellImageExtensions, false);
        Add("Video", VideoExtensions, true);
        Add("Audio", AudioExtensions, true);
        Add("Documents", DocumentExtensions, false);
        return categories;
    }

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);
}
