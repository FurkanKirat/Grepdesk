using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Grepdesk.Core;
using Grepdesk.Core.Editor;

namespace Grepdesk.Windows;

[SupportedOSPlatform("windows")]
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

    // HKCU keys: no admin rights needed, and only affect the current user.
    //   Directory\Background\shell → right-click on empty space inside a folder (%V = that folder)
    //   Directory\shell            → right-click on a folder itself          (%1 = that folder)
    private const string ContextMenuKeyName = "Grepdesk";
    private static readonly (string ParentKey, string Arg)[] ContextMenuKeys =
    [
        (@"Software\Classes\Directory\Background\shell", "%V"),
        (@"Software\Classes\Directory\shell", "%1")
    ];

    public bool SupportsFolderContextMenu => true;

    public bool IsFolderContextMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ContextMenuKeys[0].ParentKey}\{ContextMenuKeyName}");
        return key is not null;
    }

    public ShellActionResult RegisterFolderContextMenu(string executablePath, string label)
    {
        try
        {
            foreach (var (parentKey, arg) in ContextMenuKeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{parentKey}\{ContextMenuKeyName}");
                key.SetValue("", label);
                key.SetValue("Icon", $"\"{executablePath}\"");

                using var command = key.CreateSubKey("command");
                command.SetValue("", $"\"{executablePath}\" \"{arg}\"");
            }
            return ShellActionResult.Success(null);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
        }
    }

    public ShellActionResult UnregisterFolderContextMenu()
    {
        try
        {
            foreach (var (parentKey, _) in ContextMenuKeys)
                Registry.CurrentUser.DeleteSubKeyTree($@"{parentKey}\{ContextMenuKeyName}", throwOnMissingSubKey: false);
            return ShellActionResult.Success(null);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
        }
    }
}
