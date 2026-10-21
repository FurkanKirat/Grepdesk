using Grepdesk.Core.Browse;

namespace Grepdesk.Tests;

public class BrowseTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 14, 0, 0);

    private static FolderEntry File(string name, long size = 0, DateTime? modified = null) =>
        new(Path.Combine("X", name), name, false, size, modified ?? T0, FileAttributes.Normal);

    private static FolderEntry Folder(string name) => new(Path.Combine("X", name), name, true, 0, T0, FileAttributes.Directory);

    // ---- listing ----

    [Fact]
    public void Lists_direct_children_only()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.txt", "hello");
        dir.WriteFile(Path.Combine("sub", "deep.txt"), "x");

        var contents = FolderListing.List(dir.Path, showHidden: false);

        Assert.True(contents.IsReadable);
        Assert.Equal(["a.txt", "sub"], contents.Entries.Select(e => e.Name).Order());
        var file = contents.Entries.Single(e => e.Name == "a.txt");
        Assert.Equal(5, file.Size);
        Assert.False(file.IsDirectory);
        Assert.True(contents.Entries.Single(e => e.Name == "sub").IsDirectory);
    }

    [Fact]
    public void Hidden_files_are_listed_only_when_asked()
    {
        if (!OperatingSystem.IsWindows()) return; // "hidden" is an attribute on Windows, a leading dot elsewhere

        using var dir = new TempDir();
        var hidden = dir.WriteFile("secret.txt", "x");
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);
        dir.WriteFile("plain.txt", "x");

        Assert.Equal(["plain.txt"], FolderListing.List(dir.Path, showHidden: false).Entries.Select(e => e.Name));
        Assert.Equal(2, FolderListing.List(dir.Path, showHidden: true).Entries.Count);
    }

    [Fact]
    public void A_missing_folder_is_reported_not_thrown()
    {
        using var dir = new TempDir();
        var contents = FolderListing.List(dir.Combine("nope"), showHidden: false);
        Assert.Equal(FolderListingError.NotFound, contents.Error);
        Assert.Empty(contents.Entries);
    }

    // ---- sorting and filtering ----

    [Fact]
    public void Folders_come_first_and_names_sort_naturally()
    {
        List<FolderEntry> entries = [File("file10.txt"), File("file2.txt"), Folder("zeta"), File("File1.txt"), Folder("alpha")];

        entries.Sort(FolderListing.Comparer(new FolderSort()));

        Assert.Equal(["alpha", "zeta", "File1.txt", "file2.txt", "file10.txt"], entries.Select(e => e.Name));
    }

    [Fact]
    public void Descending_keeps_folders_first()
    {
        List<FolderEntry> entries = [File("b.txt", 1), Folder("dir"), File("a.txt", 50)];

        entries.Sort(FolderListing.Comparer(new FolderSort(FolderSortColumn.Size, Descending: true)));

        Assert.Equal(["dir", "a.txt", "b.txt"], entries.Select(e => e.Name));
    }

    [Fact]
    public void Same_type_or_date_falls_back_to_the_name()
    {
        List<FolderEntry> entries = [File("b.pdf"), File("c.txt"), File("a.pdf")];

        entries.Sort(FolderListing.Comparer(new FolderSort(FolderSortColumn.Type)));

        Assert.Equal(["a.pdf", "b.pdf", "c.txt"], entries.Select(e => e.Name));
    }

    [Fact]
    public void Toggling_a_column_flips_it_and_a_new_column_starts_like_Explorer()
    {
        var sort = new FolderSort();
        Assert.True(sort.Toggle(FolderSortColumn.Name).Descending);
        Assert.True(sort.Toggle(FolderSortColumn.Size).Descending);      // largest first
        Assert.True(sort.Toggle(FolderSortColumn.Modified).Descending);  // newest first
        Assert.False(sort.Toggle(FolderSortColumn.Type).Descending);
    }

    [Fact]
    public void Filter_matches_part_of_the_name_ignoring_case()
    {
        List<FolderEntry> entries = [File("Report.pdf"), File("notes.txt"), Folder("Reports")];

        Assert.Equal(["Report.pdf", "Reports"], FolderListing.Filter(entries, " report ").Select(e => e.Name));
        Assert.Equal(3, FolderListing.Filter(entries, "").Count());
    }

    // ---- history ----

    [Fact]
    public void Back_and_forward_walk_the_history()
    {
        var history = new NavigationHistory();
        history.Navigate(@"C:\a");
        history.Navigate(@"C:\b");
        history.Navigate(@"C:\c");

        Assert.Equal(@"C:\b", history.GoBack());
        Assert.Equal(@"C:\a", history.GoBack());
        Assert.False(history.CanGoBack);
        Assert.Equal(@"C:\b", history.GoForward());
        Assert.True(history.CanGoForward);

        // Going somewhere new from the middle drops the forward list.
        history.Navigate(@"C:\d");
        Assert.False(history.CanGoForward);
        Assert.Equal([@"C:\b", @"C:\a"], history.BackEntries);
    }

    [Fact]
    public void Navigating_to_the_current_folder_adds_nothing()
    {
        var history = new NavigationHistory();
        history.Navigate(@"C:\a");
        history.Navigate(@"C:\a\");
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Forget_drops_a_deleted_folder_and_its_children()
    {
        var history = new NavigationHistory();
        foreach (var p in new[] { @"C:\a", @"C:\gone", @"C:\gone\inner", @"C:\a", @"C:\b" })
            history.Navigate(p);

        history.Forget(@"C:\gone");

        // C:\a, C:\gone, C:\gone\inner, C:\a → C:\a, C:\a → one C:\a.
        Assert.Equal([@"C:\a"], history.BackEntries);
        Assert.Equal(@"C:\b", history.Current);
    }

    // ---- address bar ----

    [Fact]
    public void Resolves_typed_folders_files_and_relative_paths()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile(Path.Combine("sub", "a.txt"), "x");
        var sub = dir.Combine("sub");

        Assert.Equal(new AddressTarget(AddressKind.Folder, sub), AddressResolver.Resolve($"\"{sub}\\\"", null));
        Assert.Equal(new AddressTarget(AddressKind.File, file), AddressResolver.Resolve(file, null));
        Assert.Equal(new AddressTarget(AddressKind.Folder, sub), AddressResolver.Resolve("sub", dir.Path));
        Assert.Equal(new AddressTarget(AddressKind.Folder, dir.Path), AddressResolver.Resolve("..", sub));
        Assert.Equal(AddressKind.NotFound, AddressResolver.Resolve("missing", dir.Path).Kind);
        Assert.Equal(AddressKind.NotFound, AddressResolver.Resolve("sub", null).Kind);
        Assert.Equal(AddressKind.NotFound, AddressResolver.Resolve("   ", dir.Path).Kind);
    }

    [Fact]
    public void Shell_locations_are_passed_through()
    {
        Assert.Equal(new AddressTarget(AddressKind.Shell, "shell:startup"), AddressResolver.Resolve(" shell:startup ", null));
        Assert.Equal(AddressKind.Shell, AddressResolver.Resolve("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", null).Kind);
    }

    [Fact]
    public void Environment_variables_and_bare_drive_letters_are_expanded()
    {
        if (!OperatingSystem.IsWindows()) return;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(new AddressTarget(AddressKind.Folder, profile), AddressResolver.Resolve("%USERPROFILE%", null));

        var drive = Path.GetPathRoot(profile)!; // "C:\"
        Assert.Equal(new AddressTarget(AddressKind.Folder, drive), AddressResolver.Resolve(drive[..2], profile));
    }

    [Fact]
    public void Breadcrumbs_start_at_the_root()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(
            [new AddressSegment("C:", @"C:\"), new AddressSegment("Users", @"C:\Users"), new AddressSegment("me", @"C:\Users\me")],
            AddressResolver.Segments(@"C:\Users\me\"));
        Assert.Equal([new AddressSegment("D:", @"D:\")], AddressResolver.Segments(@"D:\"));
        Assert.Equal(
            [new AddressSegment(@"\\server\share", @"\\server\share\"), new AddressSegment("docs", @"\\server\share\docs")],
            AddressResolver.Segments(@"\\server\share\docs"));
    }

    // ---- names ----

    [Theory]
    [InlineData("report.pdf", NameProblem.None)]
    [InlineData("  ", NameProblem.Empty)]
    [InlineData("..", NameProblem.Empty)]
    [InlineData("a/b", NameProblem.InvalidCharacters)]
    public void Validates_names_everywhere(string name, NameProblem expected) =>
        Assert.Equal(expected, FileNames.Validate(name));

    [Theory]
    [InlineData("a?.txt", NameProblem.InvalidCharacters)]
    [InlineData("CON", NameProblem.Reserved)]
    [InlineData("nul.txt", NameProblem.Reserved)]
    [InlineData("console.txt", NameProblem.None)]
    [InlineData("name.", NameProblem.EndsWithDotOrSpace)]
    [InlineData("name ", NameProblem.EndsWithDotOrSpace)]
    public void Validates_Windows_naming_rules(string name, NameProblem expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, FileNames.Validate(name));
    }

    [Fact]
    public void Unique_names_count_up_like_Explorer()
    {
        using var dir = new TempDir();
        Assert.Equal("New folder", FileNames.UniqueName(dir.Path, "New folder", isDirectory: true));

        Directory.CreateDirectory(dir.Combine("New folder"));
        Directory.CreateDirectory(dir.Combine("New folder (2)"));
        Assert.Equal("New folder (3)", FileNames.UniqueName(dir.Path, "New folder", isDirectory: true));

        dir.WriteFile("notes.txt", "x");
        Assert.Equal("notes (2).txt", FileNames.UniqueName(dir.Path, "notes.txt", isDirectory: false));
    }

    [Fact]
    public void Creates_a_folder_with_a_free_name()
    {
        using var dir = new TempDir();
        var first = FileNames.CreateFolder(dir.Path, "New folder");
        var second = FileNames.CreateFolder(dir.Path, "New folder");

        Assert.Equal(dir.Combine("New folder"), first);
        Assert.Equal(dir.Combine("New folder (2)"), second);
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public void Renames_files_and_folders()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("a.txt", "x");
        Directory.CreateDirectory(dir.Combine("old"));

        var renamed = FileNames.Rename(file, " b.txt ");
        Assert.Equal(new RenameResult(RenameOutcome.Renamed, dir.Combine("b.txt")), renamed);
        Assert.True(System.IO.File.Exists(dir.Combine("b.txt")));

        Assert.Equal(RenameOutcome.Renamed, FileNames.Rename(dir.Combine("old"), "new").Outcome);
        Assert.True(Directory.Exists(dir.Combine("new")));
    }

    [Fact]
    public void Rename_refuses_to_overwrite_and_reports_bad_names()
    {
        using var dir = new TempDir();
        var a = dir.WriteFile("a.txt", "a");
        dir.WriteFile("b.txt", "b");

        Assert.Equal(RenameOutcome.AlreadyExists, FileNames.Rename(a, "b.txt").Outcome);
        Assert.Equal("b", System.IO.File.ReadAllText(dir.Combine("b.txt")));
        Assert.Equal(RenameOutcome.Unchanged, FileNames.Rename(a, "a.txt").Outcome);
        Assert.Equal(new RenameResult(RenameOutcome.InvalidName, a, NameProblem.InvalidCharacters), FileNames.Rename(a, "x/y"));
        Assert.Equal(RenameOutcome.NotFound, FileNames.Rename(dir.Combine("gone.txt"), "c.txt").Outcome);
    }

    [Fact]
    public void Rename_can_change_only_the_case()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var file = dir.WriteFile("readme.md", "x");
        Directory.CreateDirectory(dir.Combine("docs"));

        Assert.Equal(RenameOutcome.Renamed, FileNames.Rename(file, "README.md").Outcome);
        Assert.Equal(RenameOutcome.Renamed, FileNames.Rename(dir.Combine("docs"), "Docs").Outcome);

        var names = Directory.EnumerateFileSystemEntries(dir.Path).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["Docs", "README.md"], names);
    }
}
