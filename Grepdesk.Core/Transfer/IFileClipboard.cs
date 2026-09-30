namespace Grepdesk.Core.Transfer;

/// <summary>Files put on the clipboard by the file manager's Copy (Ctrl+C) or Cut (Ctrl+X).</summary>
public sealed record ClipboardFiles(IReadOnlyList<string> Paths, bool IsCut);

public interface IFileClipboard
{
    /// <summary>The copied or cut files, or null if the clipboard holds no files.</summary>
    ClipboardFiles? GetFiles();

    /// <summary>After a successful cut-and-paste the files are gone from their source, so the clipboard is emptied (as Explorer does).</summary>
    void Clear();
}
