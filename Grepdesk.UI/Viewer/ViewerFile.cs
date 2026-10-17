using System.Globalization;
using System.Text;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

internal enum ViewerProblem { None, TooLarge, Binary, LongLines, Unreadable }

/// <summary>How a text file's bytes are written: saving uses the same encoding, and the same byte order mark.</summary>
internal sealed record FileEncoding(Encoding Encoding, bool Bom, string Name)
{
    public static readonly FileEncoding Utf8 = new(new UTF8Encoding(false), false, "UTF-8");
    public static readonly FileEncoding Utf8Bom = new(new UTF8Encoding(true), true, "UTF-8 BOM");
    public static readonly FileEncoding Utf16Le = new(new UnicodeEncoding(false, true), true, "UTF-16 LE");
    public static readonly FileEncoding Utf16Be = new(new UnicodeEncoding(true, true), true, "UTF-16 BE");
}

/// <summary>A text file read for the viewer, or why it can't be shown.</summary>
internal sealed record ViewerFile(string Text, FileEncoding? Encoding, int LineCount, ViewerProblem Problem)
{
    public string EncodingName => Encoding?.Name ?? "";

    public static ViewerFile Failed(ViewerProblem problem) => new("", null, 0, problem);
}

/// <summary>
/// Reads text files for the viewer's editor and writes them back. Which
/// files count as text is decided by <see cref="ViewerFormats"/>; a file of
/// an unknown type is still opened as text when its bytes look like text.
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

    static ViewerFileLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Text, code and Markdown: the files shown in the editor.</summary>
    public static bool CanView(string path) => ViewerFormats.KindOf(path) is ViewerKind.Text or ViewerKind.Markdown;

    public static bool IsMarkdown(string path) => ViewerFormats.KindOf(path) == ViewerKind.Markdown;

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
    internal static (string Text, FileEncoding Encoding)? Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), FileEncoding.Utf8Bom);
        if (bytes is [0xFF, 0xFE, ..])
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), FileEncoding.Utf16Le);
        if (bytes is [0xFE, 0xFF, ..])
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), FileEncoding.Utf16Be);

        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, BinaryProbeBytes)) >= 0) return null;

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), FileEncoding.Utf8);
        }
        catch (DecoderFallbackException)
        {
            var legacy = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (legacy.GetString(bytes), new FileEncoding(legacy, false, legacy.WebName));
        }
    }

    /// <summary>
    /// Writes the text back in the encoding it was read with. When it no longer
    /// fits (a character the legacy code page lacks), the file becomes UTF-8.
    /// </summary>
    /// <returns>The encoding actually used.</returns>
    public static FileEncoding Save(string path, string text, FileEncoding encoding)
    {
        byte[] body;
        try
        {
            var strict = Encoding.GetEncoding(encoding.Encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            body = strict.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            encoding = FileEncoding.Utf8;
            body = encoding.Encoding.GetBytes(text);
        }

        // Encoded in full before the file is opened: a failure above leaves it untouched.
        var preamble = encoding.Bom ? encoding.Encoding.GetPreamble() : [];
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(preamble);
        stream.Write(body);
        return encoding;
    }
}
