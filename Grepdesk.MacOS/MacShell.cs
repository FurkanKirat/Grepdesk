using System;
using System.Diagnostics;
using System.IO;
using Grepdesk.Core;
using Grepdesk.Core.Editor;

namespace Grepdesk.MacOS;

public class MacShell : IPlatformShell
{
    public ShellActionResult OpenPath(string path)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
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
            var process = Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = true });
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
            var process = Process.Start(new ProcessStartInfo("open", $"-a Terminal \"{directoryPath}\"") { UseShellExecute = true });
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
            var process = Process.Start(new ProcessStartInfo("open", $"-a \"{editor.ExecutablePath}\" \"{target}\"")
            {
                UseShellExecute = true
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
            var full = Path.Combine(dir, exeName); // uzantısız
            if (File.Exists(full)) return full;
        }
        return null;
    }
}