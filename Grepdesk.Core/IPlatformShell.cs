using Grepdesk.Core.Editor;

namespace Grepdesk.Core;

public interface IPlatformShell
{
    ShellActionResult OpenPath(string path);
    ShellActionResult ShowInFileManager(string path);
    ShellActionResult OpenInTerminal(string directoryPath);
    ShellActionResult OpenInEditor(AvailableEditor editor, string resultPath);
    string? FindExecutableOnPath(string exeName);

    /// <summary>Moves a file or folder to the Recycle Bin / Trash, so the user can restore it.</summary>
    ShellActionResult MoveToTrash(string path);
}