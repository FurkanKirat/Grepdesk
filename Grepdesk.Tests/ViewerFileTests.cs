using System.Text;
using Grepdesk.UI.Viewer;

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
    public void Only_text_files_are_viewable(string name, bool expected) =>
        Assert.Equal(expected, ViewerFileLoader.CanView(Path.Combine("C:", "x", name)));

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

    [Fact]
    public void Byte_order_marks_pick_the_encoding()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hi")).ToArray();
        Assert.Equal(("hi", "UTF-16 LE"), ViewerFileLoader.Decode(utf16));

        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hi")).ToArray();
        Assert.Equal(("hi", "UTF-8 BOM"), ViewerFileLoader.Decode(utf8));
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_the_legacy_code_page()
    {
        // "Hı" in Windows-1254; on its own 0xFD is never valid UTF-8.
        var decoded = ViewerFileLoader.Decode([0x48, 0xFD]);

        Assert.NotNull(decoded);
        Assert.DoesNotContain('�', decoded.Value.Text);
        Assert.NotEqual("UTF-8", decoded.Value.Encoding);
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
