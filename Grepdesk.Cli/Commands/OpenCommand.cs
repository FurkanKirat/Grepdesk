using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grepdesk.Core;
using Grepdesk.Core.Browse;
using Grepdesk.Core.ContentSearch;
using Grepdesk.Core.Editor;
using Grepdesk.Core.Grouping;
using UglyToad.PdfPig;

namespace Grepdesk.Cli.Commands;

internal static class OpenCommand
{
    public sealed record PdfPageDto(int Page, string Text);

    public sealed record OpenFileReportDto(
        string Path,
        string Kind,
        string Format,
        long Size,
        int TotalLines,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartLine,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndLine,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TotalPages,
        string Content,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PdfPageDto>? Pages);

    public sealed record OpenDirEntryDto(
        string Name,
        string Type,
        bool IsDirectory,
        long Size,
        DateTime Modified);

    public sealed record OpenDirectoryReportDto(
        string Path,
        string Kind,
        int TotalEntries,
        IReadOnlyList<OpenDirEntryDto> Entries);

    public sealed record OpenLaunchDto(
        string Path,
        string Target,
        bool Launched);

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private const long MaxFileSizeBytes = 50L * 1024 * 1024;
    private const int BinaryProbeBytes = 8000;

    static OpenCommand() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IPlatformShell platformShell,
        CancellationToken ct)
    {
        string? targetPath = null;
        bool openGui = false;
        bool openDefault = false;
        bool revealInFolder = false;
        bool lineNumbers = false;
        bool includeHidden = false;
        bool jsonOutput = false;
        (int Start, int End)? lineRange = null;
        int? headLines = null;
        int? tailLines = null;
        (int Start, int End)? pageRange = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp(stdout);
                    return 0;

                case "-g":
                case "--gui":
                case "--app":
                    openGui = true;
                    break;

                case "-d":
                case "--default":
                case "--system":
                    openDefault = true;
                    break;

                case "-r":
                case "--reveal":
                    revealInFolder = true;
                    break;

                case "-n":
                case "--line-numbers":
                    lineNumbers = true;
                    break;

                case "-l":
                case "--lines":
                    if (i + 1 >= args.Length || !TryParseRange(args[++i], out var lr))
                        return Fail(stderr, "Invalid --lines value. Expected <line> or <start>-<end> (1-based).");
                    lineRange = lr;
                    break;

                case "--head":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var h) || h < 1)
                        return Fail(stderr, "Invalid --head value. Must be a positive integer.");
                    headLines = h;
                    break;

                case "--tail":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var t) || t < 1)
                        return Fail(stderr, "Invalid --tail value. Must be a positive integer.");
                    tailLines = t;
                    break;

                case "-P":
                case "--page":
                case "--pages":
                    if (i + 1 >= args.Length || !TryParseRange(args[++i], out var pr))
                        return Fail(stderr, "Invalid --page value. Expected <page> or <start>-<end> (1-based).");
                    pageRange = pr;
                    break;

                case "--hidden":
                    includeHidden = true;
                    break;

                case "--json":
                    jsonOutput = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return Fail(stderr, $"Unknown option '{arg}'. Run 'grepdesk open --help' for usage.");
                    if (targetPath is null)
                        targetPath = arg;
                    else
                        return Fail(stderr, $"Unexpected positional argument '{arg}'.");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(targetPath))
            return Fail(stderr, "Missing required <path> argument. Run 'grepdesk open --help' for usage.");

        int launchModes = (openGui ? 1 : 0) + (openDefault ? 1 : 0) + (revealInFolder ? 1 : 0);
        if (launchModes > 1)
            return Fail(stderr, "Options --gui, --default, and --reveal cannot be combined.");

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        bool isFile = File.Exists(fullPath);
        bool isDir = !isFile && Directory.Exists(fullPath);

        if (!isFile && !isDir)
            return Fail(stderr, $"Path not found: {fullPath}");

        if (openDefault || revealInFolder || openGui)
            return await ExecuteLaunchModeAsync(fullPath, isDir, openGui, openDefault, revealInFolder, jsonOutput, stdout, stderr, platformShell);

        if (isDir)
            return await OutputDirectoryAsync(fullPath, includeHidden, lineNumbers, lineRange, headLines, tailLines, jsonOutput, stdout, stderr, ct);

        return await OutputFileAsync(fullPath, lineNumbers, lineRange, headLines, tailLines, pageRange, jsonOutput, stdout, stderr, ct);
    }

    private static async Task<int> ExecuteLaunchModeAsync(
        string fullPath,
        bool isDir,
        bool openGui,
        bool openDefault,
        bool revealInFolder,
        bool jsonOutput,
        TextWriter stdout,
        TextWriter stderr,
        IPlatformShell platformShell)
    {
        ShellActionResult result;
        string targetName;

        if (openDefault)
        {
            targetName = "default";
            result = platformShell.OpenPath(fullPath);
        }
        else if (revealInFolder)
        {
            targetName = "file-manager";
            result = platformShell.ShowInFileManager(fullPath);
        }
        else
        {
            targetName = "grepdesk-gui";
            var uiExe = ResolveGrepdeskUiExecutable(platformShell);
            var editor = new AvailableEditor(
                "Grepdesk",
                uiExe,
                isDir ? EditorTargetKind.Directory : EditorTargetKind.File);
            result = platformShell.OpenInEditor(editor, fullPath);
        }

        if (!result.IsSuccess)
        {
            var reason = result.Exception?.Message ?? result.Status.ToString();
            return Fail(stderr, $"Failed to open '{fullPath}' ({targetName}): {reason}");
        }

        if (jsonOutput)
        {
            var dto = new OpenLaunchDto(fullPath.Replace('\\', '/'), targetName, true);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(dto, JsonIndented));
        }
        else
        {
            await stdout.WriteLineAsync($"Opened '{fullPath}' ({targetName}).");
        }

        return 0;
    }

    private static string ResolveGrepdeskUiExecutable(IPlatformShell shell)
    {
        var exeName = OperatingSystem.IsWindows() ? "Grepdesk.UI.exe" : "Grepdesk.UI";
        var sibling = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(sibling))
            return sibling;

        var onPath = shell.FindExecutableOnPath("Grepdesk.UI");
        if (!string.IsNullOrEmpty(onPath))
            return onPath;

        return sibling;
    }

    private static async Task<int> OutputDirectoryAsync(
        string fullPath,
        bool includeHidden,
        bool lineNumbers,
        (int Start, int End)? lineRange,
        int? headLines,
        int? tailLines,
        bool jsonOutput,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        var listing = FolderListing.List(fullPath, showHidden: includeHidden, ct);
        if (!listing.IsReadable)
            return Fail(stderr, $"Unable to read directory '{fullPath}': {listing.Error}");

        var sorted = listing.Entries.ToList();
        sorted.Sort(FolderListing.Comparer(new FolderSort()));

        var dtoEntries = new List<OpenDirEntryDto>(sorted.Count);
        foreach (var e in sorted)
        {
            var bucket = FolderGrouper.BucketOf(new GroupMember(e.FullPath, e.IsDirectory, Math.Max(0, e.Size), e.Modified));
            var typeLabel = e.IsDirectory ? "dir" : BucketToShortType(bucket);
            dtoEntries.Add(new OpenDirEntryDto(
                e.Name,
                typeLabel,
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Size,
                e.Modified));
        }

        if (jsonOutput)
        {
            var report = new OpenDirectoryReportDto(
                fullPath.Replace('\\', '/'),
                "directory",
                dtoEntries.Count,
                dtoEntries);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(report, JsonIndented));
            return 0;
        }

        var lines = dtoEntries
            .Select(e => e.IsDirectory
                ? $"{e.Name}/"
                : $"{e.Name,-40} {CliFormatting.FormatBytes(e.Size),10}")
            .ToArray();

        var (startIdx, endIdx) = ResolveSliceBounds(lines.Length, lineRange, headLines, tailLines);
        for (int i = startIdx; i < endIdx; i++)
        {
            if (lineNumbers)
                await stdout.WriteLineAsync($"{i + 1,4}: {lines[i]}");
            else
                await stdout.WriteLineAsync(lines[i]);
        }

        return 0;
    }

    private static async Task<int> OutputFileAsync(
        string fullPath,
        bool lineNumbers,
        (int Start, int End)? lineRange,
        int? headLines,
        int? tailLines,
        (int Start, int End)? pageRange,
        bool jsonOutput,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        var info = new FileInfo(fullPath);
        if (info.Length > MaxFileSizeBytes)
            return Fail(stderr, $"File exceeds maximum size limit ({CliFormatting.FormatBytes(MaxFileSizeBytes)}): {fullPath}");

        var ext = Path.GetExtension(fullPath);
        var format = string.IsNullOrEmpty(ext) ? "text" : ext.TrimStart('.').ToLowerInvariant();

        string content;
        int? totalPages = null;
        List<PdfPageDto>? pdfPages = null;

        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pdfResult = ExtractPdfPages(fullPath, pageRange, ct);
            if (pdfResult is null)
                return Fail(stderr, $"Unable to extract text from PDF: {fullPath}");

            (totalPages, pdfPages, content) = pdfResult.Value;
        }
        else if (ExtractorRegistry.TryGetExtractor(ext, out var extractor) && extractor is not PlainTextExtractor)
        {
            var extracted = await extractor.ExtractTextAsync(fullPath, ct);
            if (extracted is null)
                return Fail(stderr, $"Unable to extract text from {format.ToUpperInvariant()} document: {fullPath}");
            content = extracted.TrimEnd('\r', '\n');
        }
        else
        {
            var decoded = await TryReadTextFileAsync(fullPath, ct);
            if (decoded is null)
                return Fail(stderr, $"Binary or unsupported file format '{ext}'. Use --gui or --default to open it.");
            content = decoded;
        }

        var allLines = SplitLines(content);
        var (startIdx, endIdx) = ResolveSliceBounds(allLines.Length, lineRange, headLines, tailLines);
        var selectedLines = allLines[startIdx..endIdx];

        if (jsonOutput)
        {
            var slicedContent = string.Join('\n', selectedLines);
            bool isSliced = startIdx > 0 || endIdx < allLines.Length;
            var report = new OpenFileReportDto(
                fullPath.Replace('\\', '/'),
                "file",
                format,
                info.Length,
                allLines.Length,
                isSliced && selectedLines.Length > 0 ? startIdx + 1 : null,
                isSliced && selectedLines.Length > 0 ? endIdx : null,
                totalPages,
                slicedContent,
                pdfPages);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(report, JsonIndented));
            return 0;
        }

        for (int i = 0; i < selectedLines.Length; i++)
        {
            int lineNum = startIdx + i + 1;
            if (lineNumbers)
                await stdout.WriteLineAsync($"{lineNum,4}: {selectedLines[i]}");
            else
                await stdout.WriteLineAsync(selectedLines[i]);
        }

        return 0;
    }

    private static (int TotalPages, List<PdfPageDto> Pages, string CombinedText)? ExtractPdfPages(
        string fullPath,
        (int Start, int End)? pageRange,
        CancellationToken ct)
    {
        try
        {
            using var pdf = PdfDocument.Open(fullPath);
            int totalPages = pdf.NumberOfPages;
            int startPage = pageRange?.Start ?? 1;
            int endPage = pageRange.HasValue ? Math.Min(totalPages, pageRange.Value.End) : totalPages;

            var pages = new List<PdfPageDto>();
            var sb = new StringBuilder();

            for (int p = startPage; p <= endPage && p <= totalPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                if (p < 1) continue;
                var page = pdf.GetPage(p);
                var pageText = (page.Text ?? "").Trim();
                pages.Add(new PdfPageDto(p, pageText));

                if (sb.Length > 0)
                    sb.Append('\n');
                if (totalPages > 1 && (!pageRange.HasValue || pageRange.Value.Start != pageRange.Value.End))
                    sb.Append($"--- Page {p} ---\n");
                sb.Append(pageText);
            }

            return (totalPages, pages, sb.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryReadTextFileAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length == 0)
            return "";

        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..])
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..])
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, BinaryProbeBytes)) >= 0)
            return null;

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var legacy = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return legacy.GetString(bytes);
        }
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];

        return normalized.Split('\n');
    }

    private static (int StartIndex, int EndIndex) ResolveSliceBounds(
        int totalLines,
        (int Start, int End)? lineRange,
        int? headLines,
        int? tailLines)
    {
        if (totalLines == 0)
            return (0, 0);

        int start = 0;
        int end = totalLines;

        if (lineRange.HasValue)
        {
            start = Math.Clamp(lineRange.Value.Start - 1, 0, totalLines);
            end = Math.Clamp(lineRange.Value.End, start, totalLines);
        }
        else if (headLines.HasValue)
        {
            end = Math.Min(totalLines, headLines.Value);
        }
        else if (tailLines.HasValue)
        {
            start = Math.Max(0, totalLines - tailLines.Value);
        }

        return (start, end);
    }

    private static bool TryParseRange(string raw, out (int Start, int End) range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var span = raw.AsSpan().Trim();
        int sepIdx = span.IndexOfAny('-', ':');
        if (sepIdx < 0)
        {
            if (int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out int single) && single >= 1)
            {
                range = (single, single);
                return true;
            }
            return false;
        }

        var left = span[..sepIdx].Trim();
        var right = span[(sepIdx + 1)..].Trim();

        if (!int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) || start < 1)
            return false;
        if (!int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) || end < start)
            return false;

        range = (start, end);
        return true;
    }

    private static string BucketToShortType(TypeBucket bucket) => bucket switch
    {
        TypeBucket.Archives => "archive",
        TypeBucket.Code => "code",
        TypeBucket.Images => "image",
        TypeBucket.Documents => "doc",
        TypeBucket.Videos => "video",
        TypeBucket.Audio => "audio",
        TypeBucket.Installers => "installer",
        TypeBucket.DiskImages => "disk-image",
        TypeBucket.Fonts => "font",
        TypeBucket.Torrents => "torrent",
        TypeBucket.Folders => "dir",
        _ => "file"
    };

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"Error: {message}");
        return 2;
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage: grepdesk open <path> [options]
            Aliases: cat, read, view

            Outputs file or directory contents in the CLI by default (including extracted
            text from .pdf, .docx, and .xlsx), or opens the path in Grepdesk UI or the OS
            default application.

            CLI Output Options:
              -n, --line-numbers     Show 1-based line numbers
              -l, --lines <s-e>      Show only lines in range (e.g. 10-50 or 25)
              --head <n>             Show first N lines
              --tail <n>             Show last N lines
              -P, --page <s-e>       Extract specific PDF page or range (e.g. 2 or 1-3)
              --hidden               Include hidden and system entries when listing a directory
              --json                 Output structured JSON with metadata and content

            Launch Options:
              -g, --gui              Open path in the Grepdesk Desktop UI (viewer or browser)
              -d, --default          Open path with the OS default application
              -r, --reveal           Reveal path in the OS file manager (Explorer/Finder)
              -h, --help             Show this help message
            """);
    }
}
