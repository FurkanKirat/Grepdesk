using System.Text;
using Grepdesk.Core.Viewer;
using Grepdesk.UI.Viewer;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Grepdesk.Tests;

public class ViewerFileTests
{
    [Theory]
    [InlineData("notes.txt", true)]
    [InlineData("Program.CS", true)]
    [InlineData("README.md", true)]
    [InlineData("Dockerfile", true)]
    [InlineData("photo.png", false)]
    [InlineData("slides.pptx", false)]
    [InlineData("noextension", false)]
    public void Only_text_files_open_in_the_editor(string name, bool expected) =>
        Assert.Equal(expected, ViewerFileLoader.CanView(Path.Combine("C:", "x", name)));

    [Theory]
    [InlineData("notes.txt", ViewerKind.Text)]
    [InlineData("README.MD", ViewerKind.Markdown)]
    [InlineData("photo.JPG", ViewerKind.Image)]
    [InlineData("phone.heic", ViewerKind.ShellImage)]
    [InlineData("manual.pdf", ViewerKind.Pdf)]
    [InlineData("clip.mkv", ViewerKind.Video)]
    [InlineData("song.flac", ViewerKind.Audio)]
    [InlineData("report.docx", ViewerKind.Document)]
    [InlineData("setup.exe", ViewerKind.Other)]
    [InlineData("noextension", ViewerKind.Other)]
    public void Every_file_gets_a_way_to_be_shown(string name, ViewerKind expected) =>
        Assert.Equal(expected, ViewerFormats.KindOf(Path.Combine("C:", "x", name)));

    [Fact]
    public void Open_with_lists_real_extensions_only()
    {
        var extensions = ViewerFormats.OpenWithExtensions.ToList();

        Assert.Contains(".txt", extensions);
        Assert.Contains(".pdf", extensions);
        Assert.Contains(".mp4", extensions);
        Assert.DoesNotContain(".gitignore", extensions);
        Assert.DoesNotContain(".bat", extensions);
        Assert.DoesNotContain(".cmd", extensions);
        Assert.DoesNotContain(".ps1", extensions);
        Assert.Equal(extensions.Count, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void File_type_groups_hold_each_associable_type_once()
    {
        var all = ViewerFormats.FileTypeCategories.SelectMany(c => c.Extensions).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(all, ViewerFormats.LaunchedExtensions.Contains);
        Assert.Contains(".heic", all);
        Assert.Contains(".docx", all);
        Assert.Contains(".cs", ViewerFormats.FileTypeCategories.Single(c => c.Id == "Code").Extensions);
        Assert.Contains(".txt", ViewerFormats.FileTypeCategories.Single(c => c.Id == "Text").Extensions);
    }

    [Fact]
    public void Default_associations_leave_out_types_shown_only_in_part()
    {
        var defaults = ViewerFormats.OpenWithExtensions.ToList();

        Assert.DoesNotContain(".heic", defaults);
        Assert.DoesNotContain(".docx", defaults);
        Assert.Subset(ViewerFormats.AssociableExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase), defaults.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Utf8_text_is_read_whole_with_its_line_count()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.cs", "line one\nşğüİ two\nthree");

        var file = ViewerFileLoader.Load(path, default);

        Assert.Equal(ViewerProblem.None, file.Problem);
        Assert.Equal("line one\nşğüİ two\nthree", file.Text);
        Assert.Equal(3, file.LineCount);
        Assert.Equal("UTF-8", file.EncodingName);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[0])]
    public void Saving_keeps_the_encoding_and_byte_order_mark(byte[] bom)
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.txt");
        var encoding = bom.Length == 2 ? Encoding.Unicode : Encoding.UTF8;
        File.WriteAllBytes(path, [.. bom, .. encoding.GetBytes("eski\r\nmetin")]);
        var file = ViewerFileLoader.Load(path, default);

        var used = ViewerFileLoader.Save(path, file.Text.Replace("eski", "yeni ş"), file.Encoding!);

        Assert.Equal(file.Encoding, used);
        Assert.Equal([.. bom, .. encoding.GetBytes("yeni ş\r\nmetin")], File.ReadAllBytes(path));
    }

    [Fact]
    public void Characters_the_legacy_code_page_lacks_switch_the_file_to_utf8()
    {
        using var dir = new TempDir();
        var path = dir.Combine("old.txt");
        File.WriteAllBytes(path, [0x48, 0xFD]);
        var file = ViewerFileLoader.Load(path, default);
        Assert.NotEqual("UTF-8", file.EncodingName);

        var used = ViewerFileLoader.Save(path, file.Text + " 😀", file.Encoding!);

        Assert.Equal(FileEncoding.Utf8, used);
        Assert.Equal(file.Text + " 😀", File.ReadAllText(path));
    }

    [Fact]
    public void Pdf_pages_are_measured_and_drawn()
    {
        using var dir = new TempDir();
        var path = dir.Combine("doc.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Merhaba", 24, new UglyToad.PdfPig.Core.PdfPoint(72, 720), font);
        builder.AddPage(PageSize.A4, isPortrait: false);
        File.WriteAllBytes(path, builder.Build());

        using var renderer = PdfRenderer.Open(path, default);

        Assert.Equal(2, renderer.TotalPages);
        Assert.Equal(595, renderer.PageSizes[0].Width, 0);
        Assert.True(renderer.PageSizes[1].Width > renderer.PageSizes[1].Height);
        var page = renderer.Render(0, 0.5);
        Assert.NotNull(page);
        Assert.Equal(page.Width * page.Height * 4, page.Pixels.Length);
        Assert.Contains(page.Pixels, b => b != 0); // the text left some ink
    }

    [Fact]
    public void Byte_order_marks_pick_the_encoding()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hi")).ToArray();
        Assert.Equal(("hi", FileEncoding.Utf16Le), ViewerFileLoader.Decode(utf16));

        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hi")).ToArray();
        Assert.Equal(("hi", FileEncoding.Utf8Bom), ViewerFileLoader.Decode(utf8));
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_the_legacy_code_page()
    {
        // "Hı" in Windows-1254; on its own 0xFD is never valid UTF-8.
        var decoded = ViewerFileLoader.Decode([0x48, 0xFD]);

        Assert.NotNull(decoded);
        Assert.DoesNotContain('�', decoded.Value.Text);
        Assert.NotEqual("UTF-8", decoded.Value.Encoding.Name);
    }

    [Fact]
    public void Binary_files_are_refused()
    {
        using var dir = new TempDir();
        var path = dir.Combine("data.txt");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x01, 0x02]);

        Assert.Equal(ViewerProblem.Binary, ViewerFileLoader.Load(path, default).Problem);
    }

    [Fact]
    public void Files_over_the_limit_are_refused()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("big.log", new string('x', 2000));

        Assert.Equal(ViewerProblem.TooLarge, ViewerFileLoader.Load(path, default, maxBytes: 1000).Problem);
    }

    [Fact]
    public void Minified_files_with_huge_lines_are_refused()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("app.min.js", "a\n" + new string('x', ViewerFileLoader.MaxLineLength + 1) + "\nb");

        Assert.Equal(ViewerProblem.LongLines, ViewerFileLoader.Load(path, default).Problem);
    }
}
