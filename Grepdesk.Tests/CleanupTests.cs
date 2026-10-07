using Grepdesk.Core;
using Grepdesk.Core.Cleanup;
using Grepdesk.Core.DiskUsage;

namespace Grepdesk.Tests;

public class CleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0);

    private static async Task<List<CleanupSuggestion>> Analyze(TempDir tmp)
    {
        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);
        var ctx = new CleanupContext(tmp.Path, tmp.Combine("Temp"), tmp.Combine("Downloads"), Now);
        // The test folder itself lives under the real AppData\Local\Temp; classify inside it.
        return CleanupAnalyzer.Analyze(index, tmp.Path, ctx, CancellationToken.None,
            p => DiskClassifier.ClassifyDetailed(Path.DirectorySeparatorChar + Path.GetRelativePath(tmp.Path, p)));
    }

    [Fact]
    public async Task Finds_temp_caches_package_caches_old_node_modules_and_old_downloads()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("Temp/setup123/a.cab", new string('t', 900), Now.AddDays(-3));
        tmp.WriteFile("Temp/fresh.log", new string('t', 50), Now.AddHours(-1));
        tmp.WriteFile("AppData/Local/Google/Chrome/User Data/Default/Cache/Cache_Data/f_0001", new string('c', 400));
        tmp.WriteFile("AppData/Local/Google/Chrome/User Data/Default/Service Worker/CacheStorage/abc/data", new string('w', 250));
        tmp.WriteFile("AppData/Local/NVIDIA/DXCache/x.nvph", new string('s', 120));
        tmp.WriteFile("AppData/Local/npm-cache/_cacache/x", new string('n', 300));
        tmp.WriteFile("code/old/node_modules/react/index.js", new string('m', 700), Now.AddDays(-200));
        tmp.WriteFile("code/new/node_modules/react/index.js", new string('m', 600), Now.AddDays(-2));
        tmp.WriteFile("Downloads/installer.exe", new string('d', 800), Now.AddDays(-120));
        tmp.WriteFile("Downloads/today.pdf", new string('d', 100), Now.AddDays(-1));

        var suggestions = (await Analyze(tmp)).ToDictionary(s => s.Kind);

        var temp = suggestions[CleanupKind.TempFiles];
        Assert.Equal(950, temp.Bytes);
        Assert.True(temp.Items.Single(i => i.Path.EndsWith("setup123")).SelectedByDefault);
        Assert.False(temp.Items.Single(i => i.Path.EndsWith("fresh.log")).SelectedByDefault); // still in use, probably

        var caches = suggestions[CleanupKind.Caches].Items;
        var browser = caches.Single(i => i.Path.EndsWith(Path.Combine("Cache", "Cache_Data")));
        Assert.True(browser.ContentsOnly);
        Assert.True(browser.SelectedByDefault);
        var offline = caches.Single(i => i.Path.Contains("Service Worker"));
        Assert.False(offline.SelectedByDefault);                     // may be a web app's offline data
        Assert.Equal(CleanupNotes.OfflineWebData, offline.Note);
        Assert.Equal(CleanupNotes.ShaderCache, caches.Single(i => i.Path.Contains("DXCache")).Note);

        var dev = suggestions[CleanupKind.DeveloperCaches].Items;
        Assert.True(dev.Single(i => i.Path.Contains("npm-cache")).SelectedByDefault);
        Assert.True(dev.Single(i => i.Path.Contains(Path.Combine("old", "node_modules"))).SelectedByDefault);
        Assert.False(dev.Single(i => i.Path.Contains(Path.Combine("new", "node_modules"))).SelectedByDefault);

        var download = Assert.Single(suggestions[CleanupKind.OldDownloads].Items);
        Assert.EndsWith("installer.exe", download.Path);
        Assert.Equal(CleanupAction.MoveToTrash, suggestions[CleanupKind.OldDownloads].Action);
    }

    [Fact]
    public async Task Duplicates_need_identical_contents_not_just_the_same_size()
    {
        using var tmp = new TempDir();
        var big = new string('a', 300_000);
        tmp.WriteFile("one/photo.jpg", big);
        tmp.WriteFile("two/photo (1).jpg", big);
        tmp.WriteFile("three/copy.jpg", big);
        tmp.WriteFile("other/same-size.jpg", new string('a', 299_999) + "b"); // differs only in the last byte
        tmp.WriteFile("small/x.txt", "dup");
        tmp.WriteFile("small/y.txt", "dup");

        var files = Directory.EnumerateFiles(tmp.Path, "*", SearchOption.AllDirectories).Select(SearchResult.FromDisk);
        var groups = await DuplicateFinder.FindAsync(files, minSize: 1, null, CancellationToken.None);

        var photos = groups.Single(g => g.FileSize == 300_000);
        Assert.Equal(3, photos.Files.Count);
        Assert.Equal(600_000, photos.WastedBytes);
        Assert.Contains(groups, g => g.FileSize == 3 && g.Files.Count == 2);
        Assert.DoesNotContain(groups.SelectMany(g => g.Files), f => f.FileName == "same-size.jpg");
    }

    [Fact]
    public void Deleting_a_cache_empties_it_but_keeps_the_folder_and_never_follows_links()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("cache/a.bin", new string('x', 100));
        tmp.WriteFile("cache/sub/b.bin", new string('x', 50));
        var outside = tmp.WriteFile("precious/keep.txt", "keep");

        var linked = false;
        try
        {
            Directory.CreateSymbolicLink(tmp.Combine("cache", "link"), tmp.Combine("precious"));
            linked = true;
        }
        catch (IOException) { }                  // no symlink privilege: the rest still runs
        catch (UnauthorizedAccessException) { }

        var item = new CleanupItem(tmp.Combine("cache"), 150, true, Now, true, ContentsOnly: true);
        var result = new CleanupExecutor(new NoShell()).Run(CleanupAction.DeletePermanently, [item], CancellationToken.None);

        Assert.Equal(150, result.FreedBytes);
        Assert.Equal(0, result.SkippedFiles);
        Assert.True(Directory.Exists(tmp.Combine("cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp.Combine("cache")));
        Assert.True(File.Exists(outside));
        if (linked) Assert.False(Directory.Exists(tmp.Combine("cache", "link")));
    }

    [Fact]
    public void A_file_in_use_is_skipped_and_reported()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix lets open files be deleted

        using var tmp = new TempDir();
        tmp.WriteFile("cache/free.bin", new string('x', 10));
        var busy = tmp.WriteFile("cache/busy.bin", new string('x', 20));

        using (File.Open(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var item = new CleanupItem(tmp.Combine("cache"), 30, true, Now, true);
            var result = new CleanupExecutor(new NoShell()).Run(CleanupAction.DeletePermanently, [item], CancellationToken.None);

            Assert.Equal(10, result.FreedBytes);
            Assert.True(result.SkippedFiles >= 1);
            Assert.Empty(result.Done); // something was left, so the item isn't reported as done
        }
    }

    [Fact]
    public void A_partly_deleted_item_reports_what_was_freed_and_what_was_left()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        tmp.WriteFile("a/free.bin", new string('x', 10));
        var busy = tmp.WriteFile("a/busy.bin", new string('x', 20));
        tmp.WriteFile("b/all.bin", new string('x', 5));

        using (File.Open(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var items = new[]
            {
                new CleanupItem(tmp.Combine("a"), 30, true, Now, true),
                new CleanupItem(tmp.Combine("b"), 5, true, Now, true),
            };
            var reports = new List<CleanupProgress>();
            var result = new CleanupExecutor(new NoShell()).Run(CleanupAction.DeletePermanently, items, CancellationToken.None,
                new SyncProgress<CleanupProgress>(reports.Add));

            var a = result.Outcomes.Single(o => o.Item == items[0]);
            Assert.Equal(10, a.Freed);
            Assert.False(a.Complete);
            Assert.True(result.Outcomes.Single(o => o.Item == items[1]).Complete);
            Assert.Equal(15, result.FreedBytes);
            Assert.Equal(new CleanupProgress(15, 2, 2), reports[^1]);
        }
    }

    [Fact]
    public async Task RemoveMany_drops_deleted_trees_and_emptied_folder_contents_in_one_pass()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("gone/a.txt", "x");
        tmp.WriteFile("gone/deep/b.txt", "x");
        tmp.WriteFile("cache/c.bin", "x");
        tmp.WriteFile("cache/sub/d.bin", "x");
        tmp.WriteFile("keep/e.txt", "x");
        tmp.WriteFile("gone-not/f.txt", "x");

        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);
        index.RemoveMany([tmp.Combine("gone")], [tmp.Combine("cache")]);

        var left = index.Search("").Select(r => Path.GetRelativePath(tmp.Path, r.FullPath)).ToHashSet();
        Assert.DoesNotContain("gone", left);
        Assert.DoesNotContain(Path.Combine("gone", "deep", "b.txt"), left);
        Assert.Contains("cache", left);                                   // the emptied folder itself stays
        Assert.DoesNotContain(Path.Combine("cache", "sub", "d.bin"), left);
        Assert.Contains(Path.Combine("keep", "e.txt"), left);
        Assert.Contains(Path.Combine("gone-not", "f.txt"), left);         // same prefix, different folder
    }

    [Theory]
    [InlineData(@"C:\Users\Ayse\AppData\Local\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\DXCache")]
    [InlineData(@"C:\Users\Ayse\AppData\Roaming\discord\Cache\Cache_Data", @"%APPDATA%\discord\Cache\Cache_Data")]
    [InlineData(@"C:\Users\Ayse\Downloads\Tax return 2025.pdf", @"%USERPROFILE%\Downloads\<file>.pdf")]
    [InlineData(@"C:\Users\Ayse\Pictures\Wedding\IMG_0001.JPG", @"%USERPROFILE%\Pictures\<folder>\<file>.JPG")]
    [InlineData(@"C:\Users\Ayse\code\shop\node_modules", @"%USERPROFILE%\code\shop\node_modules")]
    [InlineData(@"D:\Backups\ayse-laptop\old.zip", @"D:\Backups\<user>-laptop\old.zip")]
    [InlineData(@"C:\Program Files\Adobe\Photoshop", @"C:\Program Files\Adobe\Photoshop")]
    public void Anonymizer_hides_the_person_but_keeps_the_software(string path, string expected)
    {
        if (!OperatingSystem.IsWindows()) return; // Windows-style paths

        var anonymizer = new PathAnonymizer(@"C:\Users\Ayse", @"C:\Users\Ayse\AppData\Local", @"C:\Users\Ayse\AppData\Roaming");
        Assert.Equal(expected, anonymizer.Anonymize(path));
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this reports inline so the test sees every report.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class NoShell : IPlatformShell
    {
        public ShellActionResult OpenPath(string path) => throw new NotSupportedException();
        public ShellActionResult ShowInFileManager(string path) => throw new NotSupportedException();
        public ShellActionResult OpenInTerminal(string directoryPath) => throw new NotSupportedException();
        public ShellActionResult OpenInEditor(Grepdesk.Core.Editor.AvailableEditor editor, string resultPath) => throw new NotSupportedException();
        public string? FindExecutableOnPath(string exeName) => null;
        public ShellActionResult MoveToTrash(string path) => throw new NotSupportedException();
        public long? GetTrashSize() => null;
        public ShellActionResult EmptyTrash() => throw new NotSupportedException();
    }
}
