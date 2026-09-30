using System.IO.Compression;
using System.Text;
using Grepdesk.Core.Archive;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Tests;

public class ZipExtractorTests
{
    private static JobContext NewContext(ConflictChoice choice = ConflictChoice.Overwrite, CancellationToken ct = default) =>
        new(new FixedResolver(choice), ct);

    private static string MakeZip(TempDir tmp, params (string Name, string Content)[] entries)
    {
        var zipPath = tmp.Combine($"archive-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            if (content.Length > 0 || !name.EndsWith('/'))
            {
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        return zipPath;
    }

    [Fact]
    public async Task Extracts_all_files_with_content_and_folders()
    {
        using var tmp = new TempDir();
        var source = tmp.Combine("src");
        tmp.WriteFile("src/a.txt", "alpha");
        tmp.WriteFile("src/sub/deep/b.txt", new string('x', 3_000_000));
        tmp.WriteFile("src/şöğüçı ünicode.txt", "türkçe");
        tmp.WriteFile("src/empty.txt", "");
        Directory.CreateDirectory(tmp.Combine("src", "empty-dir"));
        var zip = tmp.Combine("src.zip");
        ZipFile.CreateFromDirectory(source, zip);

        var dest = tmp.Combine("out");
        var ctx = NewContext();
        await ZipExtractor.ExtractAsync([new(zip, dest)], ctx, workerCount: 4);

        Assert.Empty(ctx.Errors);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal(3_000_000, new FileInfo(Path.Combine(dest, "sub", "deep", "b.txt")).Length);
        Assert.Equal("türkçe", File.ReadAllText(Path.Combine(dest, "şöğüçı ünicode.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(dest, "empty.txt")).Length);
        Assert.True(Directory.Exists(Path.Combine(dest, "empty-dir")));

        var snap = ctx.Progress.Snapshot();
        Assert.Equal(snap.TotalBytes, snap.DoneBytes);
        Assert.Equal(4, snap.DoneFiles);
    }

    [Fact]
    public async Task Rejects_entries_that_escape_the_destination()
    {
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, ("../evil.txt", "boom"), ("ok.txt", "fine"));
        var dest = tmp.Combine("out");

        var ctx = NewContext();
        await ZipExtractor.ExtractAsync([new(zip, dest)], ctx, 2);

        Assert.False(File.Exists(tmp.Combine("evil.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "ok.txt")));
        Assert.Contains(ctx.Errors, e => e.Kind == JobErrorKind.UnsafePath);
    }

    [Fact]
    public void Extract_here_uses_parent_when_archive_has_a_single_root_folder()
    {
        using var tmp = new TempDir();
        var single = MakeZip(tmp, ("proje/", ""), ("proje/a.txt", "a"), ("proje/b/c.txt", "c"));
        var loose = MakeZip(tmp, ("a.txt", "a"), ("proje/b.txt", "b"));

        Assert.Equal(tmp.Path, ZipExtractor.ResolveExtractHereDestination(single));
        Assert.Equal(Path.Combine(tmp.Path, Path.GetFileNameWithoutExtension(loose)),
                     ZipExtractor.ResolveExtractHereDestination(loose));
    }

    [Fact]
    public async Task Identical_existing_file_is_skipped_without_asking()
    {
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, ("a.txt", "same"));
        var dest = tmp.Combine("out");
        await ZipExtractor.ExtractAsync([new(zip, dest)], NewContext(), 1);

        var resolver = new FixedResolver(ConflictChoice.Overwrite);
        await ZipExtractor.ExtractAsync([new(zip, dest)], new JobContext(resolver, default), 1);

        Assert.Equal(0, resolver.Asked);
    }

    [Theory]
    [InlineData(ConflictChoice.Skip, "old", false)]
    [InlineData(ConflictChoice.Overwrite, "new content", false)]
    [InlineData(ConflictChoice.KeepBoth, "old", true)]
    public async Task Different_existing_file_asks_and_obeys(ConflictChoice choice, string expected, bool copyExpected)
    {
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, ("a.txt", "new content"));
        var dest = tmp.Combine("out");
        tmp.WriteFile("out/a.txt", "old", new DateTime(2020, 1, 1));

        var resolver = new FixedResolver(choice);
        await ZipExtractor.ExtractAsync([new(zip, dest)], new JobContext(resolver, default), 1);

        Assert.Equal(1, resolver.Asked);
        Assert.Equal(expected, File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal(copyExpected, File.Exists(Path.Combine(dest, "a (2).txt")));
    }

    [Fact]
    public async Task Cancel_mid_file_leaves_no_partial_file()
    {
        using var tmp = new TempDir();
        var src = tmp.Combine("src");
        Directory.CreateDirectory(src);
        using (var f = File.Create(Path.Combine(src, "big.bin")))
            f.SetLength(300_000_000); // zeros: tiny zip, long extraction
        var zip = tmp.Combine("big.zip");
        ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Fastest, false);
        var dest = tmp.Combine("out");

        using var cts = new CancellationTokenSource();
        var ctx = NewContext(ct: cts.Token);
        var run = ZipExtractor.ExtractAsync([new(zip, dest)], ctx, 1);

        // Freeze the worker part-way through the file, then cancel.
        while (ctx.Progress.Snapshot().DoneBytes == 0 && !run.IsCompleted) { }
        ctx.Pause.Pause();
        Assert.False(run.IsCompleted, "extraction finished before it could be paused");
        Assert.True(File.Exists(Path.Combine(dest, "big.bin")));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(File.Exists(Path.Combine(dest, "big.bin")));
    }
}
