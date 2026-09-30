using System;
using System.Diagnostics;
using System.IO;
using Grepdesk.Core;
using Grepdesk.Core.Editor;

namespace Grepdesk.Linux;

public class LinuxShell : IPlatformShell
{
    public ShellActionResult OpenPath(string path)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false });
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
            var process = Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false });
            return ShellActionResult.Success(process);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
        }
    }

    public ShellActionResult OpenInTerminal(string directoryPath)
    {
        // Standart yok — yaygın terminallerden ilk bulduğunu dene
        foreach (var term in new[] { "gnome-terminal", "konsole", "x-terminal-emulator", "xterm" })
        {
            var exePath = FindExecutableOnPath(term);
            if (exePath is null) continue;

            try
            {
                var process = Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = directoryPath });
                return ShellActionResult.Success(process);
            }
            catch (Exception ex)
            {
                return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
            }
        }

        return ShellActionResult.Failure(ShellActionStatus.ExecutableNotFound);
    }

    public ShellActionResult OpenInEditor(AvailableEditor editor, string resultPath)
    {
        if (!Directory.Exists(resultPath) && !File.Exists(resultPath))
            return ShellActionResult.Failure(ShellActionStatus.PathNotFound);

        if (!EditorTargetResolver.TryResolve(editor, resultPath, out var target))
            return ShellActionResult.Failure(ShellActionStatus.IncompatibleTarget);

        try
        {
            var process = Process.Start(new ProcessStartInfo(editor.ExecutablePath, $"\"{target}\"")
            {
                UseShellExecute = false
            });
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
            var full = Path.Combine(dir, exeName);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}