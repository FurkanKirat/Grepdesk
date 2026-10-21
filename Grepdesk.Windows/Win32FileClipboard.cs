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

    public bool SetFiles(ClipboardFiles files)
    {
        if (files.Paths.Count == 0)
            return false;

        // DROPFILES header, then the paths as UTF-16, each null-terminated, with one more null at the end.
        var list = string.Join('\0', files.Paths) + "\0\0";
        var drop = GlobalFrom(DropFilesSize + list.Length * 2, data =>
        {
            Marshal.WriteInt32(data, 0, DropFilesSize); // pFiles: where the list starts
            Marshal.WriteInt32(data, 16, 1);            // fWide: UTF-16
            Marshal.Copy(list.ToCharArray(), 0, data + DropFilesSize, list.Length);
        });
        var effect = GlobalFrom(4, data => Marshal.WriteInt32(data, files.IsCut ? DropEffectMove : DropEffectCopy));

        // With no owner window, EmptyClipboard leaves the clipboard ownerless and SetClipboardData may fail.
        if (drop == IntPtr.Zero || effect == IntPtr.Zero || !TryOpen(OwnerWindow()))
        {
            GlobalFree(drop);
            GlobalFree(effect);
            return false;
        }

        try
        {
            EmptyClipboard();
            // Once set, the clipboard owns the memory; only a failed set leaves it ours to free.
            if (SetClipboardData(CfHdrop, drop) == IntPtr.Zero)
            {
                GlobalFree(drop);
                GlobalFree(effect);
                return false;
            }
            if (SetClipboardData(PreferredDropEffect, effect) == IntPtr.Zero)
                GlobalFree(effect); // the files are on the clipboard; a missing hint just reads as a copy
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private const int DropFilesSize = 20; // DROPFILES: pFiles, pt.x, pt.y, fNC, fWide
    private const int DropEffectCopy = 1;
    private const uint GmemMoveable = 0x0002;

    private static IntPtr GlobalFrom(int size, Action<IntPtr> fill)
    {
        var handle = GlobalAlloc(GmemMoveable | 0x0040 /* GMEM_ZEROINIT */, (UIntPtr)size);
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;
        var data = GlobalLock(handle);
        if (data == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }
        try { fill(data); }
        finally { GlobalUnlock(handle); }
        return handle;
    }

    [ThreadStatic] private static IntPtr _owner;

    /// <summary>A message-only window to own the clipboard, made once per thread.</summary>
    private static IntPtr OwnerWindow()
    {
        if (_owner == IntPtr.Zero)
            _owner = CreateWindowExW(0, "STATIC", "Grepdesk clipboard", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return _owner;
    }

    private static readonly IntPtr HwndMessage = new(-3);

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
    private static bool TryOpen(IntPtr owner = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner))
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
}
