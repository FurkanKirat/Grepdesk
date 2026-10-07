using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public ShellActionResult MoveToTrash(string path)
    {
        try
        {
            var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"Finder\" to delete POSIX file \"{escaped}\"");
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            return process.ExitCode == 0
                ? ShellActionResult.Success(null)
                : ShellActionResult.Failure(ShellActionStatus.OperationFailed);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
        }
    }

    private static string TrashFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");

    public long? GetTrashSize()
    {
        try
        {
            var dir = new DirectoryInfo(TrashFolder);
            return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public ShellActionResult EmptyTrash()
    {
        try
        {
            var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tell application \"Finder\" to empty trash");
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            if (process.ExitCode != 0) return ShellActionResult.Failure(ShellActionStatus.OperationFailed);
            return ShellActionResult.Success(null);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
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