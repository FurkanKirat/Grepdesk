namespace Grepdesk.Core.Browse;

public enum UndoOutcome { Done, NothingToUndo, Blocked, Failed }

/// <summary>Why an undo could not be done: the file system changed since the action.</summary>
public enum UndoBlock { None, Missing, NameTaken, NotEmpty }

/// <param name="Path">Where the item is after a successful undo (the old name for a rename, the removed folder for a create).</param>
public sealed record UndoResult(UndoOutcome Outcome, UndoAction? Action = null, string? Path = null, UndoBlock Block = UndoBlock.None, Exception? Error = null);

/// <summary>One thing the user did in the browser that Ctrl+Z can take back.</summary>
public abstract record UndoAction
{
    internal abstract UndoResult Undo();
}

/// <summary>A rename, undone by renaming back to the old name.</summary>
public sealed record RenamedAction(string OldPath, string NewPath) : UndoAction
{
    internal override UndoResult Undo()
    {
        var oldName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(OldPath));
        var result = FileNames.Rename(NewPath, oldName);
        return result.Outcome switch
        {
            RenameOutcome.Renamed or RenameOutcome.Unchanged => new UndoResult(UndoOutcome.Done, this, result.Path),
            RenameOutcome.NotFound => new UndoResult(UndoOutcome.Blocked, this, NewPath, UndoBlock.Missing),
            RenameOutcome.AlreadyExists => new UndoResult(UndoOutcome.Blocked, this, OldPath, UndoBlock.NameTaken),
            _ => new UndoResult(UndoOutcome.Failed, this, NewPath, Error: result.Error),
        };
    }
}

/// <summary>
/// A new folder, undone by removing it again, but only while it is still empty:
/// undo never deletes anything the user put in it since.
/// </summary>
public sealed record CreatedFolderAction(string FolderPath) : UndoAction
{
    internal override UndoResult Undo()
    {
        if (!Directory.Exists(FolderPath)) return new UndoResult(UndoOutcome.Blocked, this, FolderPath, UndoBlock.Missing);
        try
        {
            if (Directory.EnumerateFileSystemEntries(FolderPath).Any())
                return new UndoResult(UndoOutcome.Blocked, this, FolderPath, UndoBlock.NotEmpty);
            Directory.Delete(FolderPath, recursive: false);
            return new UndoResult(UndoOutcome.Done, this, FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UndoResult(UndoOutcome.Failed, this, FolderPath, Error: ex);
        }
    }
}

/// <summary>The browser's Ctrl+Z stack, newest action first out. Kept small: old actions rarely still apply.</summary>
public sealed class UndoHistory
{
    private const int Capacity = 50;
    private readonly List<UndoAction> _actions = [];

    public bool CanUndo => _actions.Count > 0;

    public void Push(UndoAction action)
    {
        _actions.Add(action);
        if (_actions.Count > Capacity) _actions.RemoveAt(0);
    }

    /// <summary>
    /// Takes back the newest action. A blocked or failed undo is dropped too, so Ctrl+Z
    /// never gets stuck on one action that can no longer be undone.
    /// </summary>
    public UndoResult Undo()
    {
        if (_actions.Count == 0) return new UndoResult(UndoOutcome.NothingToUndo);
        var action = _actions[^1];
        _actions.RemoveAt(_actions.Count - 1);
        return action.Undo();
    }
}
