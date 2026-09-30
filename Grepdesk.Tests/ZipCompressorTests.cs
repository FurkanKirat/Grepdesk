using System.Diagnostics;
using System.IO.Compression;
using Grepdesk.Core.Archive;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Tests;

public class ZipCompressorTests
{
    private const string SevenZip = @"C:\Program Files\7-Zip\7z.exe";

    private static JobContext NewContext(CancellationToken ct = default) => new(new FixedResolver(ConflictChoice.Overwrite), ct);

    /// <summary>A mix that exercises every path: in-memory deflate, in-memory store, temp-file deflate, store-from-source.</summary>
    private static void MakeSampleTree(TempDir tmp)
    {
        tmp.WriteFile("proj/readme.txt", string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 5000)), new DateTime(2023, 4, 5, 6, 7, 8));
        tmp.WriteFile("proj/şöğüçı/türkçe ad.txt", "merhaba dünya");
        tmp.WriteFile("proj/empty.txt", "");
        Directory.CreateDirectory(tmp.Combine("proj", "empty-folder"));
        Directory.CreateDirectory(tmp.Combine("proj", "sub", "deeper"));
        for (var i = 0; i < 300; i++)
            tmp.WriteFile($"proj/sub/deeper/f{i}.cs", $"class C{i} {{ int x = {i}; }}\n" + new string('/', i));

        var random = new Random(42);
        var noise = new byte[3_000_000];
        random.NextBytes(noise);
        File.WriteAllBytes(tmp.Combine("proj", "noise.bin"), noise);           // deflate can't shrink it
        File.WriteAllBytes(tmp.Combine("proj", "photo.jpg"), noise[..500_000]); // stored by extension

        var big = new byte[40_000_000];                                        // > in-memory limit
        for (var i = 0; i < big.Length; i++) big[i] = (byte)(i % 251 < 200 ? 'a' + i % 7 : random.Next());
        File.WriteAllBytes(tmp.Combine("proj", "big.log"), big);
        var bigNoise = new byte[36_000_000];
        random.NextBytes(bigNoise);
        File.WriteAllBytes(tmp.Combine("proj", "movie.mp4"), bigNoise);        // stored straight from source
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var expectedFiles = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(expected, f)).Order().ToList();
        var actualFiles = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(actual, f)).Order().ToList();
        Assert.Equal(expectedFiles, actualFiles);

        foreach (var file in expectedFiles)
            Assert.True(File.ReadAllBytes(Path.Combine(expected, file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actual, file))), file);

        var expectedDirs = Directory.GetDirectories(expected, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(expected, d)).Order();
        var actualDirs = Directory.GetDirectories(actual, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(actual, d)).Order();
        Assert.Equal(expectedDirs, actualDirs);
    }

    [Fact]
    public async Task Round_trip_through_dotnet_reader_keeps_every_file_and_folder()
    {
        using var tmp = new TempDir();
        MakeSampleTree(tmp);
        var zip = tmp.Combine("proj.zip");

        var ctx = NewContext();
        await ZipCompressor.CompressAsync([tmp.Combine("proj")], zip, CompressionLevel.Optimal, ctx, workerCount: 4);

        Assert.Empty(ctx.Errors);
        Assert.False(File.Exists(zip + ".partial"));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));

        var outDir = tmp.Combine("out");
        ZipFile.ExtractToDirectory(zip, outDir);
        AssertSameTree(tmp.Combine("proj"), Path.Combine(outDir, "proj"));
        Assert.Equal(new DateTime(2023, 4, 5, 6, 7, 8), File.GetLastWriteTime(Path.Combine(outDir, "proj", "readme.txt")));

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(ZipWriter.MethodStored, ZipWriterTests.MethodOf(zip, "proj/photo.jpg"));
        Assert.Equal(ZipWriter.MethodDeflate, ZipWriterTests.MethodOf(zip, "proj/readme.txt"));
        Assert.Contains(archive.Entries, e => e.FullName == "proj/empty-folder/");

        var snap = ctx.Progress.Snapshot();
        Assert.Equal(snap.TotalBytes, snap.DoneBytes);
        Assert.Equal(snap.TotalFiles, snap.DoneFiles);
    }

    [Fact]
    public async Task Large_incompressible_file_with_unknown_extension_is_stored_and_large_text_is_chunked()
    {
        using var tmp = new TempDir();
        var noise = new byte[50_000_000];
        new Random(7).NextBytes(noise);
        File.WriteAllBytes(tmp.Combine("disk.img"), noise); // not in the extension list: the probe must catch it
        tmp.WriteFile("server.log", string.Concat(Enumerable.Range(0, 1_500_000).Select(i => $"{i} GET /api/items/{i % 977} 200\n")));

        var zip = tmp.Combine("out.zip");
        await ZipCompressor.CompressAsync([tmp.Combine("disk.img"), tmp.Combine("server.log")], zip, CompressionLevel.Optimal, NewContext(), 8);

        Assert.Equal(ZipWriter.MethodStored, ZipWriterTests.MethodOf(zip, "disk.img"));
        Assert.Equal(ZipWriter.MethodDeflate, ZipWriterTests.MethodOf(zip, "server.log"));

        var outDir = tmp.Combine("x");
        ZipFile.ExtractToDirectory(zip, outDir);
        Assert.True(noise.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(outDir, "disk.img"))));
        Assert.Equal(File.ReadAllText(tmp.Combine("server.log")), File.ReadAllText(Path.Combine(outDir, "server.log")));

        using var archive = ZipFile.OpenRead(zip);
        var log = archive.GetEntry("server.log")!;
        Assert.True(log.CompressedLength < log.Length / 3, "the chunked log should still compress well");
    }

    [Fact]
    public async Task Many_large_files_share_the_chunk_slots_without_deadlock()
    {
        using var tmp = new TempDir();
        var text = string.Concat(Enumerable.Range(0, 2_800_000).Select(i => $"{i} row {i % 31}\n"));
        for (var i = 0; i < 6; i++)
            tmp.WriteFile($"big/log{i}.txt", text);
        Assert.True(new FileInfo(tmp.Combine("big", "log0.txt")).Length > 32 * 1024 * 1024, "must exceed the in-memory limit to be chunked");

        var zip = tmp.Combine("big.zip");
        // 3 workers → 6 chunk slots shared by 3 large files at a time, each wanting many.
        var run = ZipCompressor.CompressAsync([tmp.Combine("big")], zip, CompressionLevel.Fastest, NewContext(), workerCount: 3);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2)));

        Assert.Same(run, finished);
        await run;
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(6, archive.Entries.Count);
        using var reader = new StreamReader(archive.GetEntry("big/log5.txt")!.Open());
        Assert.Equal(text, reader.ReadToEnd());
    }

    [Fact]
    public async Task Archive_passes_7zip_integrity_test()
    {
        if (!File.Exists(SevenZip))
            return; // 7-Zip not installed on this machine

        using var tmp = new TempDir();
        MakeSampleTree(tmp);
        var zip = tmp.Combine("proj.zip");
        await ZipCompressor.CompressAsync([tmp.Combine("proj")], zip, CompressionLevel.Fastest, NewContext(), 4);

        using var process = Process.Start(new ProcessStartInfo(SevenZip, $"t \"{zip}\"") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("Everything is Ok", output);
    }

    [Fact]
    public async Task Several_selected_items_share_the_parent_folder_as_root()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("dir/a.txt", "a");
        tmp.WriteFile("dir/b/c.txt", "c");

        var paths = new[] { tmp.Combine("dir", "a.txt"), tmp.Combine("dir", "b") };
        var zip = ZipCompressor.DefaultArchivePath(paths);
        Assert.Equal(tmp.Combine("dir", "dir.zip"), zip);

        await ZipCompressor.CompressAsync(paths, zip, CompressionLevel.Optimal, NewContext(), 2);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["a.txt", "b/c.txt"], archive.Entries.Select(e => e.FullName).Order());
    }

    [Fact]
    public void Default_name_follows_explorer_and_never_overwrites()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("report.pdf", "x");
        Directory.CreateDirectory(tmp.Combine("proj"));

        Assert.Equal(tmp.Combine("report.zip"), ZipCompressor.DefaultArchivePath([tmp.Combine("report.pdf")]));
        Assert.Equal(tmp.Combine("proj.zip"), ZipCompressor.DefaultArchivePath([tmp.Combine("proj")]));

        tmp.WriteFile("proj.zip", "existing");
        Assert.Equal(tmp.Combine("proj (2).zip"), ZipCompressor.DefaultArchivePath([tmp.Combine("proj")]));
    }

    [Fact]
    public async Task Cancel_removes_partial_archive_and_temp_files()
    {
        using var tmp = new TempDir();
        MakeSampleTree(tmp);
        var zip = tmp.Combine("proj.zip");

        using var cts = new CancellationTokenSource();
        var ctx = NewContext(cts.Token);
        var run = ZipCompressor.CompressAsync([tmp.Combine("proj")], zip, CompressionLevel.SmallestSize, ctx, 2);
        while (ctx.Progress.Snapshot().DoneBytes == 0 && !run.IsCompleted) { }
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(File.Exists(zip));
        Assert.False(File.Exists(zip + ".partial"));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));
    }
}

public class ZipAcrossDrivesTests
{
    [Fact]
    public async Task Compress_on_another_drive_then_extract_back_to_the_system_drive()
    {
        using var local = new TempDir();
        using var other = OtherDrive.CreateTempDir();
        if (other is null)
            return; // single-drive machine

        other.WriteFile("proj/a.txt", string.Concat(Enumerable.Repeat("abc ", 10_000)));
        other.WriteFile("proj/sub/b.txt", "b");
        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);

        var zip = ZipCompressor.DefaultArchivePath([other.Combine("proj")]);
        await ZipCompressor.CompressAsync([other.Combine("proj")], zip, CompressionLevel.Optimal, ctx, 4);

        // The archive lives on D:, the extraction target on C:.
        await ZipExtractor.ExtractAsync([new(zip, local.Combine("out"))], ctx, 4);

        Assert.Empty(ctx.Errors);
        Assert.Equal("b", File.ReadAllText(local.Combine("out", "proj", "sub", "b.txt")));
        Assert.Equal(40_000, new FileInfo(local.Combine("out", "proj", "a.txt")).Length);
    }
}
