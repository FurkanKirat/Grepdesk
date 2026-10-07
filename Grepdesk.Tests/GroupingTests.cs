using Grepdesk.Core.Grouping;

namespace Grepdesk.Tests;

public class GroupingTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 14, 0, 0);

    private static GroupMember File(string name, long size = 10, DateTime? modified = null) =>
        new(Path.Combine("X", name), false, size, modified ?? T0);

    private static GroupMember Folder(string name, long size = 10) => new(Path.Combine("X", name), true, size, T0);

    [Theory]
    [InlineData("dracula_idle_4.png", "dracula_idle_*.png")]
    [InlineData("dracula_idle_12.png", "dracula_idle_*.png")]
    [InlineData("Report (1).pdf", "Report.pdf")]
    [InlineData("Report - Copy (2).pdf", "Report.pdf")]
    [InlineData("Report - Kopya.pdf", "Report.pdf")]
    [InlineData("Screenshot 2026-10-07 at 14.22.10.png", "Screenshot *.png")]
    [InlineData("Ekran görüntüsü 2026-10-07 142210.png", "Ekran görüntüsü *.png")]
    [InlineData("IMG_20261007_142210.jpg", "IMG_*.jpg")]
    [InlineData("export-3f2a9c1e7b4d5a60.csv", "export-*.csv")]
    [InlineData("invoice_a1b2c3d4-e5f6-7890-abcd-ef1234567890.pdf", "invoice_*.pdf")]
    [InlineData("setup.exe", "setup.exe")]
    [InlineData("ChatGPT Image 24 May 2025 21 13 05.png", "ChatGPT Image *.png")]
    [InlineData("ChatGPT Image 3 Jun 2025 09 41.png", "ChatGPT Image *.png")]
    [InlineData("Photo May 24, 2025 at 9.41.22 PM.jpg", "Photo *.jpg")]
    [InlineData("Toplantı 24 Mayıs 2025.docx", "Toplantı *.docx")]
    [InlineData("Mayday.mp3", "Mayday.mp3")]                           // a month inside a word is not a date
    [InlineData("march-of-the-penguins.mkv", "march-of-the-penguins.mkv")]
    public void Series_pattern_ignores_numbers_dates_ids_and_copy_markers(string name, string expected) =>
        Assert.Equal(expected, FolderGrouper.SeriesPattern(name, isDirectory: false));

    [Fact]
    public void Series_groups_similar_names_and_collects_the_rest()
    {
        var items = new List<GroupMember>
        {
            File("dracula_idle_1.png"), File("dracula_idle_2.png"), File("dracula_idle_10.png"),
            File("dracula_walk_1.png"), File("dracula_walk_2.png"),
            File("cv.pdf"), File("notes.txt"),
        };

        var groups = FolderGrouper.Group(items, GroupingMode.Series);

        var idle = groups[0];
        Assert.Equal("dracula_idle_*.png", idle.Label);
        Assert.Equal(["dracula_idle_1.png", "dracula_idle_2.png", "dracula_idle_10.png"], idle.Members.Select(m => m.Name)); // natural order
        Assert.Equal("dracula_walk_*.png", groups[1].Label);
        // cv.pdf and notes.txt fit no series: they are grouped as the remaining documents, not lost in a pile.
        var rest = groups[^1];
        Assert.Equal(SeriesKind.Rest, rest.Kind);
        Assert.Equal(nameof(TypeBucket.Documents), rest.Label);
        Assert.False(rest.IsLeftover);
        Assert.Equal(2, rest.Members.Count);
    }

    [Fact]
    public void Families_join_names_that_start_the_same_way()
    {
        var items = new List<GroupMember>
        {
            File("Lecture 1 - Intro.pdf"), File("Lecture 2 - Sorting.pdf"), File("Lecture 12 - Graphs.pdf"),
            File("Microsoft.Extensions.Logging.8.0.0.nupkg"), File("Microsoft.Extensions.Hosting.8.0.1.nupkg"),
            File("Newtonsoft.Json.13.0.3.nupkg"),
            File("thesis.pdf"), File("mystery.xyz"),
        };

        var groups = FolderGrouper.Group(items, GroupingMode.Series);

        var lectures = groups.Single(g => g.Kind == SeriesKind.Family && g.Members.Count == 3);
        Assert.Equal("Lecture", lectures.Label);
        var packages = groups.Single(g => g.Kind == SeriesKind.Family && g.Members.Count == 2);
        Assert.Equal("Microsoft.Extensions", packages.Label);

        // Newtonsoft (alone) joins the other packages by type; thesis.pdf the documents; the unknown type is last.
        Assert.Contains(groups, g => g.Kind == SeriesKind.Rest && g.Label == nameof(TypeBucket.Archives) && g.Members.Single().Name.StartsWith("Newtonsoft"));
        Assert.Contains(groups, g => g.Kind == SeriesKind.Rest && g.Label == nameof(TypeBucket.Documents));
        Assert.True(groups[^1].IsLeftover);
        Assert.Equal("mystery.xyz", groups[^1].Members.Single().Name);
    }

    [Fact]
    public void Zone_identifier_prefers_the_page_over_the_cdn()
    {
        const string content = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://www.itch.io/game/download\r\nHostUrl=https://w3g3a5v6.ssl.hwcdn.net/file.zip\r\n";
        Assert.Equal("itch.io", FolderGrouper.ParseZoneIdentifier(content));
        Assert.Equal("github.com", FolderGrouper.ParseZoneIdentifier("[ZoneTransfer]\nHostUrl=https://github.com/x/y/archive.zip"));
        Assert.Null(FolderGrouper.ParseZoneIdentifier("[ZoneTransfer]\nZoneId=3\nHostUrl=about:internet"));
    }

    [Fact]
    public void Source_groups_by_site_with_unknown_last()
    {
        var items = new[] { File("a.zip"), File("b.zip"), File("c.pdf"), File("d.txt") };
        var sites = new Dictionary<string, string?> { ["a.zip"] = "itch.io", ["b.zip"] = "itch.io", ["c.pdf"] = "uni.edu" };

        var groups = FolderGrouper.Group(items, GroupingMode.Source, p => sites.GetValueOrDefault(Path.GetFileName(p)));

        Assert.Equal(["itch.io", "uni.edu", ""], groups.Select(g => g.Label));
        Assert.True(groups[^1].IsLeftover);
    }

    [Fact]
    public void Sessions_split_on_a_half_hour_gap_newest_first()
    {
        var items = new[]
        {
            File("a", modified: T0), File("b", modified: T0.AddMinutes(10)), File("c", modified: T0.AddMinutes(35)),
            File("d", modified: T0.AddHours(5)),
        };

        var groups = FolderGrouper.Group(items, GroupingMode.Session);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["d"], groups[0].Members.Select(m => m.Name));
        Assert.Equal(T0, groups[1].Start);
        Assert.Equal(T0.AddMinutes(35), groups[1].End); // each gap under 30 minutes chains on
    }

    [Fact]
    public void Redundant_finds_extracted_archives_and_repeated_downloads()
    {
        var items = new[]
        {
            File("project.zip"), Folder("project"),
            File("assets.tar.gz"), Folder("assets"),
            File("lonely.zip"),
            File("setup.exe", 500), File("setup (1).exe", 500),
            File("report.pdf", 100), File("report (1).pdf", 101), // different size: a different file
        };

        var groups = FolderGrouper.Group(items, GroupingMode.Redundant);

        var extracted = groups.Single(g => g.Label == nameof(RedundancyKind.ExtractedArchive));
        Assert.Equal(["assets.tar.gz", "project.zip"], extracted.Members.Select(m => m.Name));
        Assert.Equal("project", extracted.Members.Single(m => m.Name == "project.zip").Related);

        var repeated = Assert.Single(groups.Single(g => g.Label == nameof(RedundancyKind.RepeatedDownload)).Members);
        Assert.Equal("setup (1).exe", repeated.Name);
        Assert.Equal("setup.exe", repeated.Related);
    }

    [Theory]
    [InlineData("dracula_idle_*.png", true, "dracula_idle")]
    [InlineData("Screenshot *.png", true, "Screenshot")]
    [InlineData("itch.io", false, "itch.io")]
    [InlineData("a:b?c", false, "a b c")]
    public void Suggested_folder_names_are_valid_and_readable(string label, bool isPattern, string expected) =>
        Assert.Equal(expected, FolderOrganizer.SuggestName(label, isPattern));

    [Fact]
    public void Organizing_moves_into_a_subfolder_and_never_overwrites()
    {
        using var tmp = new TempDir();
        var a = tmp.WriteFile("dracula_idle_1.png", "1");
        var b = tmp.WriteFile("dracula_idle_2.png", "2");
        tmp.WriteFile("dracula_idle/dracula_idle_2.png", "already there");
        var sub = tmp.Combine("pack");
        Directory.CreateDirectory(sub);

        var result = FolderOrganizer.MoveInto(tmp.Path, "dracula_idle", [a, b, sub]);

        Assert.Equal([a, sub], result.Moved);
        Assert.Equal([(b, SkipReason.NameTaken)], result.Skipped);
        Assert.True(result.FolderExisted);
        Assert.True(System.IO.File.Exists(tmp.Combine("dracula_idle", "dracula_idle_1.png")));
        Assert.True(Directory.Exists(tmp.Combine("dracula_idle", "pack")));
        Assert.Equal("already there", System.IO.File.ReadAllText(tmp.Combine("dracula_idle", "dracula_idle_2.png")));
        Assert.True(System.IO.File.Exists(b));
    }

    [Fact]
    public void Undo_puts_everything_back_and_removes_the_empty_folder()
    {
        using var tmp = new TempDir();
        var a = tmp.WriteFile("a_1.png", "1");
        var b = tmp.WriteFile("a_2.png", "2");

        var result = FolderOrganizer.MoveInto(tmp.Path, "a", [a, b]);
        Assert.False(System.IO.File.Exists(a));

        Assert.Equal(2, FolderOrganizer.Undo(result));
        Assert.True(System.IO.File.Exists(a));
        Assert.True(System.IO.File.Exists(b));
        Assert.False(Directory.Exists(tmp.Combine("a")));
    }

    [Fact]
    public void The_target_folder_itself_is_left_in_place_and_said_so()
    {
        using var tmp = new TempDir();
        var asdfg = tmp.Combine("asdfg");
        var asdfg1 = tmp.Combine("asdfg1");
        Directory.CreateDirectory(asdfg);
        Directory.CreateDirectory(asdfg1);

        Assert.Equal(TargetState.ExistingFolder, FolderOrganizer.CheckTarget(tmp.Path, "asdfg"));
        Assert.Equal("asdfg 2", FolderOrganizer.UniqueName(tmp.Path, "asdfg"));

        var result = FolderOrganizer.MoveInto(tmp.Path, "asdfg", [asdfg, asdfg1]);

        Assert.Equal([(asdfg, SkipReason.IsTarget)], result.Skipped);
        Assert.Equal([asdfg1], result.Moved);
        Assert.True(Directory.Exists(Path.Combine(asdfg, "asdfg1")));
    }

    [Fact]
    public void A_file_with_the_name_blocks_the_target()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("notes", "a file, not a folder");
        Assert.Equal(TargetState.ExistingFile, FolderOrganizer.CheckTarget(tmp.Path, "notes"));
        Assert.Equal(TargetState.Invalid, FolderOrganizer.CheckTarget(tmp.Path, "*?"));
        Assert.Equal(TargetState.New, FolderOrganizer.CheckTarget(tmp.Path, "fresh"));
    }
}
