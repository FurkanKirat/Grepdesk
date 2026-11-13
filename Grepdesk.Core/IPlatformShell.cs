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

    /// <summary>Bytes currently in the Recycle Bin / Trash (all drives), or null if unknown.</summary>
    long? GetTrashSize();

    /// <summary>Permanently deletes everything in the Recycle Bin / Trash.</summary>
    ShellActionResult EmptyTrash();

    /// <summary>True where the OS has its own Properties and "Open with" dialogs that <see cref="ShowProperties"/> and <see cref="OpenWithDialog"/> can raise.</summary>
    bool SupportsNativeDialogs => false;

    /// <summary>The OS Properties dialog for a file, folder or drive.</summary>
    ShellActionResult ShowProperties(string path) => ShellActionResult.Failure(ShellActionStatus.Unsupported);

    /// <summary>The OS "choose an app to open this file with" dialog.</summary>
    ShellActionResult OpenWithDialog(string path) => ShellActionResult.Failure(ShellActionStatus.Unsupported);
}