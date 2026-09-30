using Grepdesk.Core.Editor;

namespace Grepdesk.Core;

public interface IPlatformShell
{
    ShellActionResult OpenPath(string path);
    ShellActionResult ShowInFileManager(string path);
    ShellActionResult OpenInTerminal(string directoryPath);
    ShellActionResult OpenInEditor(AvailableEditor editor, string resultPath);
    string? FindExecutableOnPath(string exeName);
}