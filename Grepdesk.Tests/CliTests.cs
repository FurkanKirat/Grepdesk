using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Grepdesk.Cli;
using Grepdesk.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Grepdesk.Tests;

public class CliTests : IDisposable
{
    private readonly string _tempDir;

    public CliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GrepdeskCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string[] args,
        string? stdinText = null,
        IPlatformShell? shell = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var stdin = new StringReader(stdinText ?? "");
        int code = await CliRunner.RunAsync(
            args,
            stdout,
            stderr,
            stdin,
            platformShell: shell,
            driveKindProvider: new BasicDriveKindProvider(),
            isInteractive: stdinText is not null,
            showProgress: false);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Help_and_version_return_zero()
    {
        var (helpCode, helpOut, _) = await RunCliAsync(["--help"]);
        Assert.Equal(0, helpCode);
        Assert.Contains("grepdesk <command>", helpOut);

        var (verCode, verOut, _) = await RunCliAsync(["--version"]);
        Assert.Equal(0, verCode);
        Assert.Contains("grepdesk ", verOut);

        var (badCode, _, badErr) = await RunCliAsync(["nonexistent-cmd"]);
        Assert.Equal(2, badCode);
        Assert.Contains("Unknown command", badErr);
    }

    [Fact]
    public async Task Find_filters_by_pattern_type_ext_depth_and_outputs_json()
    {
        var sub = Path.Combine(_tempDir, "src", "deep");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "README.md"), "# readme");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Program.cs"), "class Program {}");
        await File.WriteAllTextAsync(Path.Combine(sub, "Helper.cs"), "class Helper {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".hidden_env"), "secret");

        // Substring match + --ext cs + --json
        var (code, stdout, _) = await RunCliAsync(["find", "Helper", "--path", _tempDir, "--ext", "cs", "--json"]);
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("src/deep/Helper.cs", doc.RootElement[0].GetProperty("path").GetString());
        Assert.Equal("code", doc.RootElement[0].GetProperty("type").GetString());

        // --max-depth 1 excludes src/deep/Helper.cs
        var (depthCode, depthOut, _) = await RunCliAsync(["find", "*.cs", "--path", _tempDir, "--max-depth", "1"]);
        Assert.Equal(0, depthCode);
        Assert.Contains("Program.cs", depthOut);
        Assert.DoesNotContain("Helper.cs", depthOut);

        // Hidden file excluded by default, included with --hidden
        var (noHiddenCode, _, _) = await RunCliAsync(["find", "hidden_env", "--path", _tempDir]);
        Assert.Equal(1, noHiddenCode);

        var (hiddenCode, hiddenOut, _) = await RunCliAsync(["find", "hidden_env", "--path", _tempDir, "--hidden"]);
        Assert.Equal(0, hiddenCode);
        Assert.Contains(".hidden_env", hiddenOut);
    }

    [Fact]
    public async Task Grep_searches_text_docx_and_pdf_with_structured_json()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "notes.md"), "Line one\nHere is API_SECRET_TOKEN in markdown\nLine three");
        CreateSampleDocx(Path.Combine(_tempDir, "spec.docx"), "Document header", "The docx contains API_SECRET_TOKEN inside a paragraph.");
        CreateSamplePdf(Path.Combine(_tempDir, "manual.pdf"), "First page text", "Second page with API_SECRET_TOKEN right here.");

        var (code, stdout, stderr) = await RunCliAsync([
            "grep", "API_SECRET_TOKEN",
            "--path", _tempDir,
            "--ext", "pdf,docx,md",
            "--json"
        ]);

        Assert.True(code == 0, $"Expected 0, stderr: {stderr}");
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("API_SECRET_TOKEN", root.GetProperty("query").GetString());
        Assert.Equal(3, root.GetProperty("total_matches").GetInt32());

        var results = root.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(results, r =>
            r.GetProperty("file").GetString() == "manual.pdf" &&
            r.GetProperty("format").GetString() == "pdf" &&
            r.GetProperty("page").GetInt32() == 2);
        Assert.Contains(results, r =>
            r.GetProperty("file").GetString() == "spec.docx" &&
            r.GetProperty("format").GetString() == "docx");
        Assert.Contains(results, r =>
            r.GetProperty("file").GetString() == "notes.md" &&
            r.GetProperty("line").GetInt32() == 2);

        // Case-sensitive search for lowercase should return 1 (no matches)
        var (missCode, _, _) = await RunCliAsync([
            "grep", "api_secret_token",
            "--path", _tempDir,
            "--case-sensitive"
        ]);
        Assert.Equal(1, missCode);
    }

    [Fact]
    public async Task Zip_and_unzip_roundtrip_and_benchmark()
    {
        var sourceDir = Path.Combine(_tempDir, "input");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), new string('A', 2048));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "b.txt"), new string('B', 4096));

        var zipPath = Path.Combine(_tempDir, "bundle.zip");
        var (zipCode, zipOut, _) = await RunCliAsync(["zip", zipPath, sourceDir, "-l", "6", "--json"]);
        Assert.Equal(0, zipCode);
        Assert.True(File.Exists(zipPath));

        using var zipJson = JsonDocument.Parse(zipOut);
        Assert.Equal(2, zipJson.RootElement.GetProperty("files").GetInt32());

        var extractDir = Path.Combine(_tempDir, "extracted");
        var (unzipCode, unzipOut, _) = await RunCliAsync(["unzip", zipPath, "-o", extractDir, "--benchmark"]);
        Assert.Equal(0, unzipCode);
        Assert.Contains("MB/s", unzipOut);
        Assert.Equal(2048, new FileInfo(Path.Combine(extractDir, "input", "a.txt")).Length);
        Assert.Equal(4096, new FileInfo(Path.Combine(extractDir, "input", "b.txt")).Length);
    }

    [Fact]
    public async Task Duplicates_finds_identical_files_and_delete_prompt_uses_MoveToTrash()
    {
        var f1 = Path.Combine(_tempDir, "copy1.bin");
        var f2 = Path.Combine(_tempDir, "copy2.bin");
        var unique = Path.Combine(_tempDir, "unique.bin");
        var payload = Encoding.UTF8.GetBytes(new string('X', 4096));
        await File.WriteAllBytesAsync(f1, payload);
        await File.WriteAllBytesAsync(f2, payload);
        await File.WriteAllBytesAsync(unique, Encoding.UTF8.GetBytes("different"));

        var (jsonCode, jsonOut, _) = await RunCliAsync(["duplicates", _tempDir, "--min-size", "1KB", "--json"]);
        Assert.Equal(0, jsonCode);
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.Equal(1, doc.RootElement.GetProperty("total_groups").GetInt32());
        Assert.Equal(4096, doc.RootElement.GetProperty("total_wasted_bytes").GetInt64());

        var fakeShell = new RecordingPlatformShell();
        var (delCode, _, _) = await RunCliAsync(
            ["duplicates", _tempDir, "--delete-prompt"],
            stdinText: "2\n",
            shell: fakeShell);
        Assert.Equal(0, delCode);
        Assert.Single(fakeShell.TrashedPaths);
        Assert.Equal(f2, fakeShell.TrashedPaths[0]);
    }

    [Fact]
    public async Task Du_summarizes_folder_usage_as_json()
    {
        var sub = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(sub);
        await File.WriteAllBytesAsync(Path.Combine(sub, "clip.mp4"), new byte[8192]);
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "readme.txt"), new byte[1024]);

        var (code, stdout, _) = await RunCliAsync(["du", _tempDir, "--top", "5", "--json"]);
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(9216, doc.RootElement.GetProperty("scanned_bytes").GetInt64());
        Assert.True(doc.RootElement.GetProperty("top_entries").GetArrayLength() >= 2);
    }

    private static void CreateSampleDocx(string path, params string[] paragraphs)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("word/document.xml");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        foreach (var p in paragraphs)
            writer.Write($"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>");
        writer.Write("</w:body></w:document>");
    }

    private static void CreateSamplePdf(string path, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(50, 700), font);
        }
        File.WriteAllBytes(path, builder.Build());
    }

    private sealed class RecordingPlatformShell : IPlatformShell
    {
        public List<string> TrashedPaths { get; } = [];

        public ShellActionResult OpenPath(string path) => ShellActionResult.Success(null);
        public ShellActionResult ShowInFileManager(string path) => ShellActionResult.Success(null);
        public ShellActionResult OpenInTerminal(string directoryPath) => ShellActionResult.Success(null);
        public ShellActionResult OpenInEditor(Grepdesk.Core.Editor.AvailableEditor editor, string resultPath) => ShellActionResult.Success(null);
        public string? FindExecutableOnPath(string exeName) => null;
        public long? GetTrashSize() => 0;
        public ShellActionResult EmptyTrash() => ShellActionResult.Success(null);
        public ShellActionResult MoveToTrash(string path)
        {
            TrashedPaths.Add(path);
            return ShellActionResult.Success(null);
        }
    }
}
