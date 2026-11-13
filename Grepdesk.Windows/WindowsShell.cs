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

    public bool SupportsNativeDialogs => true;

    /// <summary>
    /// Explorer's Properties dialog. It belongs to the calling thread, so this must be called from
    /// the UI thread (which pumps messages); from a thread without a message loop it would vanish.
    /// </summary>
    public ShellActionResult ShowProperties(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return ShellActionResult.Failure(ShellActionStatus.PathNotFound);

        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskInvokeIdList,
            lpVerb = "properties",
            lpFile = path,
            nShow = SwShow,
        };
        return ShellExecuteEx(ref info)
            ? ShellActionResult.Success(null)
            : ShellActionResult.Failure(ShellActionStatus.OperationFailed, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    /// <summary>The "How do you want to open this file?" dialog.</summary>
    public ShellActionResult OpenWithDialog(string path)
    {
        if (!File.Exists(path)) return ShellActionResult.Failure(ShellActionStatus.PathNotFound);
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                ArgumentList = { "shell32.dll,OpenAs_RunDLL", path },
                UseShellExecute = false,
            });
            return ShellActionResult.Success(process);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.ProcessStartFailed, ex);
        }
    }

    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const int SwShow = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    public ShellActionResult OpenInTerminal(string directoryPath)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                // Windows PowerShell ships with every Windows install. The folder is the working
                // directory, so no quoting of the path is needed.
                FileName = "powershell.exe",
                Arguments = "-NoExit",
                WorkingDirectory = directoryPath,
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

    public long? GetTrashSize()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : null;
    }

    public ShellActionResult EmptyTrash()
    {
        const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
        const int E_UNEXPECTED = unchecked((int)0x8000FFFF); // returned when the bin is already empty

        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        return hr is 0 or E_UNEXPECTED
            ? ShellActionResult.Success(null)
            : ShellActionResult.Failure(ShellActionStatus.OperationFailed, new IOException($"SHEmptyRecycleBin failed (0x{hr:X})"));
    }

    // shellapi.h packs its structs to 8 bytes on 64-bit Windows (natural layout here).
    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

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