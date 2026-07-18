namespace FileSearch.Core.Editor;

public class EditorDetector(IPlatformShell shell)
{
    private static readonly (string Header, string Command, EditorTargetKind TargetKind)[] KnownEditors =
    [
        ("VS Code'da Aç", "code", EditorTargetKind.Directory),
        ("Rider'da Aç", "rider", EditorTargetKind.Directory),
        ("Notepad++'da Aç", "notepad++", EditorTargetKind.File),
    ];

    public IReadOnlyList<AvailableEditor> AvailableEditors { get; } = Detect(shell);

    private static List<AvailableEditor> Detect(IPlatformShell shell)
    {
        var found = new List<AvailableEditor>();

        foreach (var (header, command, targetKind) in KnownEditors)
        {
            var exePath = shell.FindExecutableOnPath(command);
            if (exePath is not null)
                found.Add(new AvailableEditor(header, exePath, targetKind));
        }

        return found;
    }
}
