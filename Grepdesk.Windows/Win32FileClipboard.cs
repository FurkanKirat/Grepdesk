using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Grepdesk.Core.Transfer;

namespace Grepdesk.Windows;

/// <summary>
/// Reads what Explorer puts on the clipboard for Ctrl+C / Ctrl+X: the file
/// list (CF_HDROP) and the "Preferred DropEffect" DWORD that tells a cut
/// (move) from a copy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32FileClipboard : IFileClipboard
{
    private const uint CfHdrop = 15;
    private const int DropEffectMove = 2;

    private static readonly uint PreferredDropEffect = RegisterClipboardFormatW("Preferred DropEffect");

    public ClipboardFiles? GetFiles()
    {
        if (!TryOpen())
            return null;

        try
        {
            var drop = GetClipboardData(CfHdrop);
            if (drop == IntPtr.Zero)
                return null;

            var paths = new List<string>();
            var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFileW(drop, i, null, 0);
                var buffer = new StringBuilder((int)length + 1);
                DragQueryFileW(drop, i, buffer, (uint)buffer.Capacity);
                paths.Add(buffer.ToString());
            }

            return paths.Count == 0 ? null : new ClipboardFiles(paths, IsCut());
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Clear()
    {
        if (!TryOpen())
            return;
        try { EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    private static bool IsCut()
    {
        var handle = GetClipboardData(PreferredDropEffect);
        if (handle == IntPtr.Zero)
            return false; // no hint: treat as copy, the safe default

        var data = GlobalLock(handle);
        if (data == IntPtr.Zero)
            return false;
        try { return (Marshal.ReadInt32(data) & DropEffectMove) != 0; }
        finally { GlobalUnlock(handle); }
    }

    // Another program may be holding the clipboard for a moment.
    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string format);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder? file, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);
}
