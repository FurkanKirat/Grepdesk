using Grepdesk.Core.Browse;

namespace Grepdesk.Tests;

public class UndoHistoryTests
{
    [Fact]
    public void Undoing_a_rename_restores_the_old_name()
    {
        using var dir = new TempDir();
        var old = dir.WriteFile("a.txt", "hello");
        var renamed = FileNames.Rename(old, "b.txt");
        var history = new UndoHistory();
        history.Push(new RenamedAction(old, renamed.Path));

        var result = history.Undo();

        Assert.Equal(UndoOutcome.Done, result.Outcome);
        Assert.Equal(old, result.Path);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(renamed.Path));
    }

    [Fact]
    public void A_rename_is_not_undone_over_a_file_that_took_the_old_name()
    {
        using var dir = new TempDir();
        var old = dir.WriteFile("a.txt", "one");
        var renamed = FileNames.Rename(old, "b.txt");
        dir.WriteFile("a.txt", "two");
        var history = new UndoHistory();
        history.Push(new RenamedAction(old, renamed.Path));

        var result = history.Undo();

        Assert.Equal(UndoOutcome.Blocked, result.Outcome);
        Assert.Equal(UndoBlock.NameTaken, result.Block);
        Assert.Equal("two", File.ReadAllText(old));
        Assert.Equal("one", File.ReadAllText(renamed.Path));
    }

    [Fact]
    public void Undoing_a_new_folder_removes_it_only_while_empty()
    {
        using var dir = new TempDir();
        var empty = FileNames.CreateFolder(dir.Path, "New folder");
        var used = FileNames.CreateFolder(dir.Path, "Used");
        File.WriteAllText(Path.Combine(used, "keep.txt"), "x");
        var history = new UndoHistory();
        history.Push(new CreatedFolderAction(used));
        history.Push(new CreatedFolderAction(empty));

        Assert.Equal(UndoOutcome.Done, history.Undo().Outcome);
        Assert.False(Directory.Exists(empty));

        var blocked = history.Undo();
        Assert.Equal(UndoOutcome.Blocked, blocked.Outcome);
        Assert.Equal(UndoBlock.NotEmpty, blocked.Block);
        Assert.True(File.Exists(Path.Combine(used, "keep.txt")));
    }

    [Fact]
    public void Undo_goes_newest_first_and_an_empty_history_has_nothing_to_undo()
    {
        using var dir = new TempDir();
        var folder = FileNames.CreateFolder(dir.Path, "New folder");
        var renamed = FileNames.Rename(folder, "Photos");
        var history = new UndoHistory();
        history.Push(new CreatedFolderAction(folder));
        history.Push(new RenamedAction(folder, renamed.Path));

        Assert.Equal(UndoOutcome.Done, history.Undo().Outcome); // name back to "New folder"
        Assert.True(Directory.Exists(folder));
        Assert.Equal(UndoOutcome.Done, history.Undo().Outcome); // folder removed
        Assert.False(Directory.Exists(folder));
        Assert.False(history.CanUndo);
        Assert.Equal(UndoOutcome.NothingToUndo, history.Undo().Outcome);
    }
}
