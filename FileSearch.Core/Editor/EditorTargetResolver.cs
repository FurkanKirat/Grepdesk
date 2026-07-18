namespace FileSearch.Core.Editor;

public static class EditorTargetResolver
{
    public static bool TryResolve(AvailableEditor editor, string resultPath, out string target)
    {
        var isDirectory = Directory.Exists(resultPath);

        if (editor.TargetKind == EditorTargetKind.File && isDirectory)
        {
            target = "";
            return false;
        }

        target = editor.TargetKind == EditorTargetKind.Directory && !isDirectory
            ? Path.GetDirectoryName(resultPath)!
            : resultPath;

        return true;
    }

    public static bool IsCompatible(AvailableEditor editor, string resultPath)
    {
        var isDirectory = Directory.Exists(resultPath);
        return !(editor.TargetKind == EditorTargetKind.File && isDirectory);
    }
}