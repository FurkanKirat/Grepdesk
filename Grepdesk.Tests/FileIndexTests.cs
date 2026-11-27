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

    [Fact]
    public async Task CoversFolder_and_SearchUnder_scope_to_the_target_subtree()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("docs/report.txt", "hello");
        tmp.WriteFile("docs/sub/notes.txt", "world");
        tmp.WriteFile("docs2/other.txt", "nope");

        var index = await Build(tmp);

        Assert.True(index.CoversFolder(tmp.Path));
        Assert.True(index.CoversFolder(tmp.Combine("docs")));
        Assert.False(index.CoversFolder(Path.GetDirectoryName(tmp.Path)!));

        var allUnderDocs = index.SearchUnder(tmp.Combine("docs"), "").Select(r => r.FileName).ToList();
        Assert.Contains("report.txt", allUnderDocs);
        Assert.Contains("notes.txt", allUnderDocs);
        Assert.DoesNotContain("other.txt", allUnderDocs);
        Assert.DoesNotContain("docs", allUnderDocs);

        var filtered = index.SearchUnder(tmp.Combine("docs"), "note");
        Assert.Single(filtered);
        Assert.Equal("notes.txt", filtered[0].FileName);
    }

    [Fact]
    public async Task ScanProgressChanged_reports_monotonic_fraction_ending_at_one()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 10; i++)
            tmp.WriteFile($"sub{i}/file{i}.txt", "hello");

        var index = new FileIndex();
        var fractions = new List<double>();
        index.ScanProgressChanged += p => fractions.Add(p.Fraction);

        await index.BuildIndexAsync([tmp.Path]);

        Assert.NotEmpty(fractions);
        Assert.Equal(1.0, fractions[^1]);
        for (var i = 1; i < fractions.Count; i++)
            Assert.True(fractions[i] >= fractions[i - 1], $"Fraction decreased at {i}: {fractions[i - 1]} -> {fractions[i]}");
    }

    [Fact]
    public async Task Remove_and_RemoveMany_decrement_ancestor_folder_sizes()
    {
        using var tmp = new TempDir();
        var f1 = tmp.WriteFile("a/b/one.bin", new string('x', 100));
        tmp.WriteFile("a/b/two.bin", new string('x', 250));
        tmp.WriteFile("a/c/three.bin", new string('x', 400));

        var index = await Build(tmp);
        Assert.Equal(750, Find(index, tmp.Path).Size);
        Assert.Equal(750, Find(index, tmp.Combine("a")).Size);
        Assert.Equal(350, Find(index, tmp.Combine("a", "b")).Size);

        index.Remove(f1);
        Assert.Equal(650, Find(index, tmp.Path).Size);
        Assert.Equal(650, Find(index, tmp.Combine("a")).Size);
        Assert.Equal(250, Find(index, tmp.Combine("a", "b")).Size);

        index.RemoveMany([tmp.Combine("a", "c")], []);
        Assert.Equal(250, Find(index, tmp.Path).Size);
        Assert.Equal(250, Find(index, tmp.Combine("a")).Size);

        index.RemoveMany([], [tmp.Combine("a", "b")]);
        Assert.Equal(0, Find(index, tmp.Path).Size);
        Assert.Equal(0, Find(index, tmp.Combine("a")).Size);
        Assert.Equal(0, Find(index, tmp.Combine("a", "b")).Size);
    }
}
