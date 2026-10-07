using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    public ShellActionResult MoveToTrash(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return ShellActionResult.Failure(ShellActionStatus.PathNotFound);

        var op = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };

        var code = SHFileOperation(ref op);
        return code == 0 && !op.fAnyOperationsAborted
            ? ShellActionResult.Success(null)
            : ShellActionResult.Failure(ShellActionStatus.OperationFailed,
                new IOException($"SHFileOperation failed (0x{code:X})"));
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);

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