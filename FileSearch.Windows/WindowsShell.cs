using System.Diagnostics;
using FileSearch.Core;
using FileSearch.Core.Editor;

namespace FileSearch.Windows;

public class WindowsShell : IPlatformShell
{
    public ShellActionResult OpenPath(string path)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return ShellActionResult.Success(process);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
        }
    }

    public ShellActionResult ShowInFileManager(string path)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            
            return ShellActionResult.Success(process);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
        }
    }

    public ShellActionResult OpenInTerminal(string directoryPath)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/K cd /d \"{directoryPath}\"",
                UseShellExecute = true
            });
            return ShellActionResult.Success(process);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
        }
    }

    public ShellActionResult OpenInEditor(AvailableEditor editor, string resultPath)
    {
        if (!Directory.Exists(resultPath) && !File.Exists(resultPath))
            return ShellActionResult.Failure(ShellActionStatus.PathNotFound);

        if (!EditorTargetResolver.TryResolve(editor, resultPath, out var target))
            return ShellActionResult.Failure(ShellActionStatus.IncompatibleTarget);

        try
        {
            var isScript = editor.ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        || editor.ExecutablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = isScript
                ? new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C \"\"{editor.ExecutablePath}\" \"{target}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
                : new ProcessStartInfo
                {
                    FileName = editor.ExecutablePath,
                    Arguments = $"\"{target}\"",
                    UseShellExecute = true
                };

            var process = Process.Start(psi);
            return ShellActionResult.Success(process);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
        }
    }

    public string? FindExecutableOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                var full = Path.Combine(dir, exeName + ext);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }
}