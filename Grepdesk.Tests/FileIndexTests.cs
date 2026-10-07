using Grepdesk.Core;

namespace Grepdesk.Tests;

public class FileIndexTests
{
    private static async Task<FileIndex> Build(TempDir tmp)
    {
        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);
        return index;
    }

    private static SearchResult Find(FileIndex index, string path) =>
        index.Search("").Single(r => string.Equals(r.FullPath, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Folders_get_the_total_size_of_everything_beneath_them()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("a/one.bin", new string('x', 100));
        tmp.WriteFile("a/b/two.bin", new string('x', 250));
        tmp.WriteFile("a/b/c/three.bin", new string('x', 1000));
        tmp.WriteFile("other/four.bin", new string('x', 7));
        Directory.CreateDirectory(tmp.Combine("empty"));

        var index = await Build(tmp);

        Assert.Equal(1000, Find(index, tmp.Combine("a", "b", "c")).Size);
        Assert.Equal(1250, Find(index, tmp.Combine("a", "b")).Size);
        Assert.Equal(1350, Find(index, tmp.Combine("a")).Size);
        Assert.Equal(0, Find(index, tmp.Combine("empty")).Size);
        Assert.Equal(1357, Find(index, tmp.Path).Size);
    }

    [Fact]
    public async Task Root_given_with_a_trailing_separator_still_gets_its_total()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("f.bin", new string('x', 42));

        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path + Path.DirectorySeparatorChar]);

        Assert.Equal(42, Find(index, tmp.Path).Size);
    }

    [Fact]
    public async Task Empty_query_returns_every_entry_and_a_query_matches_names_case_insensitively()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("Report.PDF", "x");
        tmp.WriteFile("sub/notes.txt", "x");

        var index = await Build(tmp);

        // root + Report.PDF + sub + notes.txt
        Assert.Equal(4, index.Search("").Count);
        var hit = Assert.Single(index.Search("report.pdf"));
        Assert.Equal("Report.PDF", hit.FileName);
        Assert.False(hit.IsDirectory);
        Assert.Equal(1, hit.Size);
    }

    [Fact]
    public async Task Remove_drops_a_folder_and_everything_under_it()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("gone/a.txt", "x");
        tmp.WriteFile("gone/deep/b.txt", "x");
        tmp.WriteFile("gone-not/c.txt", "x");

        var index = await Build(tmp);
        index.Remove(tmp.Combine("gone"));

        var names = index.Search("").Select(r => r.FileName).ToList();
        Assert.DoesNotContain("a.txt", names);
        Assert.DoesNotContain("b.txt", names);
        Assert.Contains("c.txt", names); // same prefix, different folder
    }
}
