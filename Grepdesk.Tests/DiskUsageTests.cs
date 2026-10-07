using Grepdesk.Core;
using Grepdesk.Core.DiskUsage;

namespace Grepdesk.Tests;

public class DiskUsageTests
{
    private static string P(string windowsPath) => windowsPath.Replace('\\', Path.DirectorySeparatorChar);

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Game\intro.mp4", DiskCategory.Games)]   // location beats extension
    [InlineData(@"C:\Users\me\Videos\holiday.MKV", DiskCategory.Videos)]
    [InlineData(@"C:\Users\me\Pictures\cat.heic", DiskCategory.Images)]
    [InlineData(@"C:\Users\me\code\app\node_modules\pkg\logo.png", DiskCategory.Developer)]
    [InlineData(@"C:\Users\me\.nuget\packages\x\1.0\x.nupkg", DiskCategory.Developer)]
    [InlineData(@"C:\Program Files\Tool\tool.dll", DiskCategory.Apps)]
    [InlineData(@"C:\Windows\System32\kernel32.dll", DiskCategory.System)]
    [InlineData(@"C:\pagefile.sys", DiskCategory.System)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\setup.tmp", DiskCategory.System)]
    [InlineData(@"C:\Users\me\Music\song.flac", DiskCategory.Audio)]
    [InlineData(@"C:\Users\me\Documents\cv.pdf", DiskCategory.Documents)]
    [InlineData(@"C:\Users\me\Downloads\thing.xyz", DiskCategory.Other)]
    [InlineData(@"C:\Users\me\models\llama-8b.Q4_K_M.gguf", DiskCategory.Developer)]
    [InlineData(@"C:\Users\me\.cache\huggingface\hub\model\blob", DiskCategory.Developer)] // before the generic .cache rule
    [InlineData(@"C:\Users\me\code\repo\.git\objects\ab\cdef0123", DiskCategory.Developer)]
    [InlineData(@"C:\Users\me\AppData\Local\NVIDIA\DXCache\abc.nvph", DiskCategory.System)]
    [InlineData(@"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_00a1b2", DiskCategory.System)]
    public void Classifies_by_location_first_then_extension(string path, DiskCategory expected) =>
        Assert.Equal(expected, DiskClassifier.Classify(P(path)));

    [Fact]
    public async Task Analysis_totals_each_category_and_keeps_the_largest_files()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("Videos/a.mp4", new string('v', 5000));
        tmp.WriteFile("Videos/b.mp4", new string('v', 3000));
        tmp.WriteFile("Pictures/c.jpg", new string('i', 700));
        tmp.WriteFile("misc/d.xyz", new string('o', 10));

        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);
        // The temp folder itself is under AppData\Local\Temp, which really is "System and temporary";
        // classify by the path inside it so the test sees what a user folder would.
        var report = DiskAnalyzer.Analyze(index, tmp.Path, largestPerCategory: 1, CancellationToken.None,
            p => new Classification(DiskClassifier.Classify(Path.GetRelativePath(tmp.Path, p)), 0));

        var videos = report.Categories.Single(c => c.Category == DiskCategory.Videos);
        Assert.Equal(8000, videos.Bytes);
        Assert.Equal(2, videos.Files);
        Assert.Equal(700, report.Categories.Single(c => c.Category == DiskCategory.Images).Bytes);
        Assert.Equal(8710, report.ScannedBytes);

        // One per category, largest first.
        Assert.Equal(["a.mp4", "c.jpg", "d.xyz"], report.LargestFiles.Select(f => f.File.FileName));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Elden Ring\Game\data0.bdt", @"D:\SteamLibrary\steamapps\common\Elden Ring")]
    [InlineData(@"C:\Program Files\Adobe\Photoshop\ps.exe", @"C:\Program Files\Adobe")]
    [InlineData(@"C:\code\site\node_modules\react\index.js", @"C:\code\site\node_modules")]
    [InlineData(@"C:\code\site\node_modules\a\node_modules\b\x.js", @"C:\code\site\node_modules")] // outermost wins
    [InlineData(@"C:\Windows\WinSxS\amd64_x\file.dll", @"C:\Windows\WinSxS")]
    [InlineData(@"C:\pagefile.sys", @"C:\pagefile.sys")]
    public void Location_rules_name_the_item_a_file_belongs_to(string path, string expectedGroup)
    {
        var p = P(path);
        var result = DiskClassifier.ClassifyDetailed(p);
        Assert.Equal(P(expectedGroup), p[..result.GroupLength]);
    }

    [Fact]
    public void Files_judged_by_extension_belong_to_no_item() =>
        Assert.Equal(0, DiskClassifier.ClassifyDetailed(P(@"C:\Users\me\Videos\a.mp4")).GroupLength);

    [Fact]
    public async Task Analysis_adds_up_each_item()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("Games/steamapps/common/Big Game/data/a.pak", new string('g', 4000));
        tmp.WriteFile("Games/steamapps/common/Big Game/b.exe", new string('g', 1000));
        tmp.WriteFile("Games/steamapps/common/Small Game/c.pak", new string('g', 300));

        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);
        // Classify inside the temp folder so its own AppData\Local\Temp location doesn't win.
        var report = DiskAnalyzer.Analyze(index, tmp.Path, largestPerCategory: 10, CancellationToken.None, p =>
        {
            var relative = Path.DirectorySeparatorChar + Path.GetRelativePath(tmp.Path, p);
            var c = DiskClassifier.ClassifyDetailed(relative);
            return c with { GroupLength = c.GroupLength == 0 ? 0 : c.GroupLength + tmp.Path.Length };
        });

        var games = report.Groups.Where(g => g.Category == DiskCategory.Games).ToList();
        Assert.Equal(["Big Game", "Small Game"], games.Select(g => Path.GetFileName(g.Path)));
        Assert.Equal(5000, games[0].Bytes);
        Assert.Equal(2, games[0].Files);
    }

    [Fact]
    public async Task ChildrenOf_lists_only_direct_children()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("top.txt", "x");
        tmp.WriteFile("sub/inner.txt", "x");
        tmp.WriteFile("sub/deeper/innermost.txt", "x");

        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);

        Assert.Equal(["sub", "top.txt"], index.ChildrenOf(tmp.Path).Select(c => c.FileName).Order());
        Assert.Equal(["deeper", "inner.txt"], index.ChildrenOf(tmp.Combine("sub")).Select(c => c.FileName).Order());
    }

    [Fact]
    public async Task Scan_finds_every_file_in_a_deep_and_wide_tree()
    {
        // Regression: the scan used to stop when the queue stayed empty for 30 ms,
        // even while a worker was still listing a folder whose subfolders weren't queued yet.
        using var tmp = new TempDir();
        var expected = 0;
        for (var i = 0; i < 40; i++)
        {
            var path = string.Join(Path.DirectorySeparatorChar, Enumerable.Range(0, 12).Select(d => $"b{i}_d{d}"));
            tmp.WriteFile(Path.Combine(path, "leaf.txt"), "x");
            expected++;
        }

        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);

        Assert.Equal(expected, index.Search("leaf.txt").Count);
    }

    [Fact]
    public async Task Junctions_are_listed_but_not_walked()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var tmp = new TempDir();
        tmp.WriteFile("real/big.bin", new string('x', 1000));
        try
        {
            Directory.CreateSymbolicLink(tmp.Combine("link"), tmp.Combine("real"));
        }
        catch (IOException) { return; }                  // no symlink privilege (Windows without developer mode)
        catch (UnauthorizedAccessException) { return; }

        var index = new FileIndex();
        await index.BuildIndexAsync([tmp.Path]);

        Assert.Single(index.Search("big.bin"));
        Assert.Contains(index.Search("link"), r => r.IsDirectory);
        Assert.Equal(1000, index.Search("").Single(r => r.FullPath == tmp.Path).Size);
    }
}
