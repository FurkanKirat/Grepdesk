using System.Globalization;
using System.Text;
using Grepdesk.Core.ContentSearch;

namespace Grepdesk.UI.Viewer;

internal enum ViewerProblem { None, TooLarge, Binary, LongLines, Unreadable }

/// <summary>A text file read for the viewer, or why it can't be shown.</summary>
internal sealed record ViewerFile(string Text, string EncodingName, int LineCount, ViewerProblem Problem)
{
    public static ViewerFile Failed(ViewerProblem problem) => new("", "", 0, problem);
}

/// <summary>
/// Which files the viewer opens and how they're read. Only plain text: code,
/// notes, logs, configs and Markdown. Everything else opens in its own app.
/// </summary>
internal static class ViewerFileLoader
{
    public const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Minified or generated files can be one huge line; laying that out would
    /// freeze the window, so such files are refused rather than shown slowly.
    /// </summary>
    public const int MaxLineLength = 100_000;

    private const int BinaryProbeBytes = 8000;

    private static readonly HashSet<string> MarkdownExtensions =
        new([".md", ".markdown", ".mdown", ".mkd"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TextExtensions = new(
        new PlainTextExtractor().SupportedExtensions.Concat(MarkdownExtensions).Concat(
        [
            ".rst", ".tex", ".tsv", ".srt", ".vtt", ".nfo", ".cfg", ".conf", ".cmd", ".psm1", ".zsh", ".fish",
            ".mjs", ".cjs", ".jsonc", ".kt", ".kts", ".swift", ".dart", ".lua", ".pl", ".r", ".fs", ".fsx",
            ".vb", ".xaml", ".axaml", ".props", ".targets", ".cmake", ".graphql", ".proto", ".tf", ".hcl",
            ".iss", ".gitignore", ".gitattributes", ".editorconfig", ".dockerignore", ".diff", ".patch",
        ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TextFileNames =
        new(["Dockerfile", "Makefile", "LICENSE", "README", "CHANGELOG", "CODEOWNERS"], StringComparer.OrdinalIgnoreCase);

    static ViewerFileLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool CanView(string path) =>
        TextExtensions.Contains(Path.GetExtension(path)) || TextFileNames.Contains(Path.GetFileName(path));

    public static bool IsMarkdown(string path) => MarkdownExtensions.Contains(Path.GetExtension(path));

    public static ViewerFile Load(string path, CancellationToken ct, long maxBytes = MaxBytes)
    {
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > maxBytes) return ViewerFile.Failed(ViewerProblem.TooLarge);
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ViewerFile.Failed(ViewerProblem.Unreadable);
        }
        ct.ThrowIfCancellationRequested();

        if (Decode(bytes) is not var (text, encoding)) return ViewerFile.Failed(ViewerProblem.Binary);

        int lines = 1, lineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i - lineStart > MaxLineLength) return ViewerFile.Failed(ViewerProblem.LongLines);
            lines++;
            lineStart = i + 1;
        }
        if (text.Length - lineStart > MaxLineLength) return ViewerFile.Failed(ViewerProblem.LongLines);

        return new ViewerFile(text, encoding, lines, ViewerProblem.None);
    }

    /// <summary>
    /// BOM first, then strict UTF-8, then the system's legacy code page (old
    /// Turkish or Western text files saved by Notepad and the like). Null for binary data.
    /// </summary>
    internal static (string Text, string Encoding)? Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        if (bytes is [0xFF, 0xFE, ..])
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes is [0xFE, 0xFF, ..])
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");

        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, BinaryProbeBytes)) >= 0) return null;

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            var legacy = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (legacy.GetString(bytes), legacy.WebName);
        }
    }
}
