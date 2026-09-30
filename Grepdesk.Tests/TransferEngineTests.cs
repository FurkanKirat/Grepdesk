using Grepdesk.Core.Jobs;
using Grepdesk.Core.Transfer;
using Grepdesk.Windows;

namespace Grepdesk.Tests;

public class StreamCopierTransferTests() : TransferEngineTests(new StreamFileCopier());

// Off Windows these run with the Unix copier instead, so every platform
// checks its own native copier against the same behaviour.
public class NativeCopierTransferTests() : TransferEngineTests(
    OperatingSystem.IsWindows() ? new CopyFile2Copier() : new NativeFileCopier());

/// <summary>The same behaviour must hold whichever copier does the byte copying.</summary>
public abstract class TransferEngineTests(IFileCopier copier)
{
    private readonly TransferEngine _engine = new(copier);

    private Task Run(TempDir tmp, string[] sources, string dest, bool move, IConflictResolver? resolver = null, JobContext? ctx = null) =>
        _engine.RunAsync(sources.Select(s => tmp.Combine(s)).ToList(), tmp.Combine(dest), move,
            ctx ?? new JobContext(resolver ?? new FixedResolver(ConflictChoice.Overwrite), default), workerCount: 4);

    [Fact]
    public async Task Copies_files_and_nested_folders_including_empty_ones()
    {
        using var tmp = new TempDir();
        var stamp = new DateTime(2021, 5, 6, 7, 8, 10);
        tmp.WriteFile("src/proj/a.txt", "alpha", stamp);
        tmp.WriteFile("src/proj/deep/er/b.txt", new string('b', 2_500_000));
        Directory.CreateDirectory(tmp.Combine("src", "proj", "empty"));
        tmp.WriteFile("src/single.txt", "one");
        Directory.CreateDirectory(tmp.Combine("dst"));

        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);
        await Run(tmp, ["src/proj", "src/single.txt"], "dst", move: false, ctx: ctx);

        Assert.Empty(ctx.Errors);
        Assert.Equal("alpha", File.ReadAllText(tmp.Combine("dst", "proj", "a.txt")));
        Assert.Equal(stamp, File.GetLastWriteTime(tmp.Combine("dst", "proj", "a.txt")));
        Assert.Equal(2_500_000, new FileInfo(tmp.Combine("dst", "proj", "deep", "er", "b.txt")).Length);
        Assert.True(Directory.Exists(tmp.Combine("dst", "proj", "empty")));
        Assert.Equal("one", File.ReadAllText(tmp.Combine("dst", "single.txt")));
        Assert.True(File.Exists(tmp.Combine("src", "proj", "a.txt")), "copy must keep the source");

        var snap = ctx.Progress.Snapshot();
        Assert.Equal(3, snap.DoneFiles);
        Assert.Equal(snap.TotalBytes, snap.DoneBytes);
    }

    [Fact]
    public async Task Move_on_the_same_volume_removes_the_source()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("src/proj/a.txt", "alpha");
        tmp.WriteFile("src/file.txt", "f");
        Directory.CreateDirectory(tmp.Combine("dst"));

        await Run(tmp, ["src/proj", "src/file.txt"], "dst", move: true);

        Assert.Equal("alpha", File.ReadAllText(tmp.Combine("dst", "proj", "a.txt")));
        Assert.Equal("f", File.ReadAllText(tmp.Combine("dst", "file.txt")));
        Assert.False(Directory.Exists(tmp.Combine("src", "proj")));
        Assert.False(File.Exists(tmp.Combine("src", "file.txt")));
    }

    [Fact]
    public async Task Move_into_an_existing_folder_merges_and_cleans_up()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("src/proj/new.txt", "new");
        tmp.WriteFile("src/proj/sub/deep.txt", "deep");
        tmp.WriteFile("dst/proj/old.txt", "old");

        await Run(tmp, ["src/proj"], "dst", move: true);

        Assert.Equal("new", File.ReadAllText(tmp.Combine("dst", "proj", "new.txt")));
        Assert.Equal("deep", File.ReadAllText(tmp.Combine("dst", "proj", "sub", "deep.txt")));
        Assert.Equal("old", File.ReadAllText(tmp.Combine("dst", "proj", "old.txt")));
        Assert.False(Directory.Exists(tmp.Combine("src", "proj")));
    }

    [Fact]
    public async Task Copy_into_the_same_folder_makes_a_numbered_copy()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("src/a.txt", "a");
        tmp.WriteFile("src/proj/x.txt", "x");

        await Run(tmp, ["src/a.txt", "src/proj"], "src", move: false);

        Assert.Equal("a", File.ReadAllText(tmp.Combine("src", "a (2).txt")));
        Assert.Equal("x", File.ReadAllText(tmp.Combine("src", "proj (2)", "x.txt")));
    }

    [Fact]
    public async Task Refuses_to_copy_a_folder_into_itself()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("src/proj/a.txt", "a");
        Directory.CreateDirectory(tmp.Combine("src", "proj", "inner"));

        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);
        await Run(tmp, ["src/proj"], "src/proj/inner", move: false, ctx: ctx);

        Assert.Contains(ctx.Errors, e => e.Kind == JobErrorKind.IntoItself);
        Assert.False(Directory.Exists(tmp.Combine("src", "proj", "inner", "proj")));
    }

    [Fact]
    public async Task Identical_file_is_not_asked_about()
    {
        using var tmp = new TempDir();
        var stamp = new DateTime(2022, 1, 1, 12, 0, 0);
        tmp.WriteFile("src/a.txt", "same", stamp);
        tmp.WriteFile("dst/a.txt", "same", stamp);

        var resolver = new FixedResolver(ConflictChoice.Overwrite);
        await Run(tmp, ["src/a.txt"], "dst", move: false, resolver);

        Assert.Equal(0, resolver.Asked);
    }

    [Fact]
    public async Task Move_with_identical_size_and_date_but_different_content_keeps_the_source()
    {
        using var tmp = new TempDir();
        var stamp = new DateTime(2022, 1, 1, 12, 0, 0);
        tmp.WriteFile("src/a.txt", "AAAA", stamp);
        tmp.WriteFile("dst/a.txt", "BBBB", stamp);

        await Run(tmp, ["src/a.txt"], "dst", move: true);

        Assert.True(File.Exists(tmp.Combine("src", "a.txt")), "source must survive: contents differ");
        Assert.Equal("BBBB", File.ReadAllText(tmp.Combine("dst", "a.txt")));
    }

    [Theory]
    [InlineData(ConflictChoice.Skip, "old", false)]
    [InlineData(ConflictChoice.Overwrite, "new!", false)]
    [InlineData(ConflictChoice.KeepBoth, "old", true)]
    public async Task Conflict_choices_are_obeyed(ConflictChoice choice, string expected, bool keptBoth)
    {
        using var tmp = new TempDir();
        tmp.WriteFile("src/a.txt", "new!");
        tmp.WriteFile("dst/a.txt", "old", new DateTime(2019, 1, 1));

        var resolver = new FixedResolver(choice);
        await Run(tmp, ["src/a.txt"], "dst", move: false, resolver);

        Assert.Equal(1, resolver.Asked);
        Assert.Equal(expected, File.ReadAllText(tmp.Combine("dst", "a.txt")));
        Assert.Equal(keptBoth, File.Exists(tmp.Combine("dst", "a (2).txt")));
    }

    [Fact]
    public async Task Locked_file_is_reported_and_the_rest_still_copies()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("src/locked.txt", "l");
        tmp.WriteFile("src/free.txt", "f");
        Directory.CreateDirectory(tmp.Combine("dst"));

        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);
        using (new FileStream(tmp.Combine("src", "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Run(tmp, ["src/locked.txt", "src/free.txt"], "dst", move: false, ctx: ctx);

        Assert.Contains(ctx.Errors, e => e.Kind == JobErrorKind.Locked);
        Assert.Equal("f", File.ReadAllText(tmp.Combine("dst", "free.txt")));
        Assert.False(File.Exists(tmp.Combine("dst", "locked.txt")));
    }

    // ------------------------------------------------ across drives (C: ↔ D:)

    private static void MakeTree(TempDir dir)
    {
        dir.WriteFile("src/proj/a.txt", "alpha", new DateTime(2021, 3, 4, 5, 6, 8));
        dir.WriteFile("src/proj/sub/deep/b.bin", new string('b', 3_000_000));
        Directory.CreateDirectory(dir.Combine("src", "proj", "empty"));
        dir.WriteFile("src/single.txt", "one");
    }

    private static void AssertTreeArrived(string root)
    {
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(root, "proj", "a.txt")));
        Assert.Equal(new DateTime(2021, 3, 4, 5, 6, 8), File.GetLastWriteTime(Path.Combine(root, "proj", "a.txt")));
        Assert.Equal(3_000_000, new FileInfo(Path.Combine(root, "proj", "sub", "deep", "b.bin")).Length);
        Assert.True(Directory.Exists(Path.Combine(root, "proj", "empty")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(root, "single.txt")));
    }

    private Task RunAcross(TempDir from, TempDir to, bool move, JobContext ctx) =>
        _engine.RunAsync([from.Combine("src", "proj"), from.Combine("src", "single.txt")], to.Combine("dst"), move, ctx, workerCount: 4);

    [Theory]
    [InlineData(false, false)] // copy C: → D:
    [InlineData(false, true)]  // copy D: → C:
    [InlineData(true, false)]  // move C: → D:
    [InlineData(true, true)]   // move D: → C:
    public async Task Copy_and_move_across_drives(bool move, bool fromOtherDrive)
    {
        using var local = new TempDir();
        using var other = OtherDrive.CreateTempDir();
        if (other is null)
            return; // single-drive machine

        var (from, to) = fromOtherDrive ? (other, local) : (local, other);
        MakeTree(from);
        Directory.CreateDirectory(to.Combine("dst"));

        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);
        await RunAcross(from, to, move, ctx);

        Assert.Empty(ctx.Errors);
        AssertTreeArrived(to.Combine("dst"));
        Assert.Equal(!move, Directory.Exists(from.Combine("src", "proj")));
        Assert.Equal(!move, File.Exists(from.Combine("src", "single.txt")));

        var snap = ctx.Progress.Snapshot();
        Assert.Equal(snap.TotalBytes, snap.DoneBytes);
    }

    [Fact]
    public async Task Move_across_drives_into_an_existing_folder_merges()
    {
        using var local = new TempDir();
        using var other = OtherDrive.CreateTempDir();
        if (other is null)
            return;

        MakeTree(local);
        other.WriteFile("dst/proj/old.txt", "old");

        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);
        await RunAcross(local, other, move: true, ctx);

        Assert.Empty(ctx.Errors);
        AssertTreeArrived(other.Combine("dst"));
        Assert.Equal("old", File.ReadAllText(other.Combine("dst", "proj", "old.txt")));
        Assert.False(Directory.Exists(local.Combine("src", "proj")));
    }

    [Fact]
    public async Task Conflict_across_drives_is_asked_and_skipped_file_keeps_its_source()
    {
        using var local = new TempDir();
        using var other = OtherDrive.CreateTempDir();
        if (other is null)
            return;

        MakeTree(local);
        other.WriteFile("dst/single.txt", "different", new DateTime(2019, 1, 1));

        var resolver = new FixedResolver(ConflictChoice.Skip);
        await RunAcross(local, other, move: true, new JobContext(resolver, default));

        Assert.Equal(1, resolver.Asked);
        Assert.Equal("different", File.ReadAllText(other.Combine("dst", "single.txt")));
        Assert.True(File.Exists(local.Combine("src", "single.txt")), "a skipped file must not be deleted from the source");
    }
}
