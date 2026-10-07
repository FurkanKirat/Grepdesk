using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Grepdesk.Core.Preview;

namespace Grepdesk.Windows;

/// <summary>
/// Thumbnails and metadata from Explorer's own handlers (IShellItemImageFactory
/// and the property system). Calls run on a short-lived STA thread: some
/// handlers, Office's among them, only work in a single-threaded apartment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellPreview : IShellPreview
{
    // A handler that hangs (a broken codec, a file on a sleeping network drive)
    // must not keep the preview waiting forever.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The shell only parses backslash paths.
    public ShellThumbnail? GetThumbnail(string path, int size) => RunSta(() => LoadThumbnail(Path.GetFullPath(path), size));

    public ShellMediaInfo GetMediaInfo(string path) => RunSta(() => LoadMediaInfo(Path.GetFullPath(path))) ?? new ShellMediaInfo();

    private static T? RunSta<T>(Func<T?> work) where T : class
    {
        T? result = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (COMException) { }
            catch (InvalidCastException) { } // the item doesn't implement the interface
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(Timeout) ? result : null;
    }

    private static ShellThumbnail? LoadThumbnail(string path, int size)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var obj) != 0)
            return null;

        var hbitmap = IntPtr.Zero;
        try
        {
            var factory = (IShellItemImageFactory)obj;
            if (factory.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_THUMBNAILONLY, out hbitmap) != 0)
                return null;
            return ReadPixels(hbitmap);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(obj);
        }
    }

    private static ShellThumbnail? ReadPixels(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;

        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = bm.bmWidth,
            biHeight = -bm.bmHeight, // negative: top row first
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[bm.bmWidth * bm.bmHeight * 4];

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbitmap, 0, (uint)bm.bmHeight, pixels, ref header, DIB_RGB_COLORS) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // Photo and video thumbnails come back with the alpha channel all zero,
        // which would draw as fully transparent.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        return new ShellThumbnail(bm.bmWidth, bm.bmHeight, pixels, hasAlpha);
    }

    private static ShellMediaInfo? LoadMediaInfo(string path)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem2).GUID, out var obj) != 0)
            return null;

        try
        {
            var item = (IShellItem2)obj;

            ulong? UInt64(PropertyKey key) => item.GetUInt64(ref key, out var v) == 0 ? v : null;
            uint? UInt32(PropertyKey key) => item.GetUInt32(ref key, out var v) == 0 && v > 0 ? v : null;
            int? Int32(PropertyKey key) => item.GetInt32(ref key, out var v) == 0 && v > 0 ? v : null;
            string? Text(PropertyKey key) => item.GetString(ref key, out var v) == 0 && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

            var duration = UInt64(MediaDuration);
            var frameRate = UInt32(VideoFrameRate); // frames per 1000 seconds
            var bitrate = UInt32(VideoTotalBitrate) ?? UInt32(VideoEncodingBitrate) ?? UInt32(AudioEncodingBitrate);

            return new ShellMediaInfo
            {
                Duration = duration > 0 ? TimeSpan.FromTicks((long)duration.Value) : null, // 100 ns units, same as ticks
                Width = (int?)(UInt32(VideoFrameWidth) ?? UInt32(ImageHorizontalSize)),
                Height = (int?)(UInt32(VideoFrameHeight) ?? UInt32(ImageVerticalSize)),
                FrameRate = frameRate / 1000.0,
                Bitrate = bitrate,
                Title = Text(Title),
                Artist = Text(MusicArtist) ?? Text(MusicAlbumArtist),
                Album = Text(MusicAlbumTitle),
                Slides = Int32(PresentationSlideCount),
            };
        }
        finally
        {
            Marshal.ReleaseComObject(obj);
        }
    }

    // ── Property keys (propkey.h) ───────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(string fmtid, uint pid)
    {
        public Guid FormatId = new(fmtid);
        public uint PropertyId = pid;
    }

    private const string MediaFmt = "64440490-4C8B-11D1-8B70-080036B11A03"; // also Audio.*
    private const string VideoFmt = "64440491-4C8B-11D1-8B70-080036B11A03";
    private const string ImageFmt = "6444048F-4C8B-11D1-8B70-080036B11A03";
    private const string MusicFmt = "56A3372E-CE9C-11D2-9F0E-006097C686F6";

    private static readonly PropertyKey MediaDuration = new(MediaFmt, 3);
    private static readonly PropertyKey AudioEncodingBitrate = new(MediaFmt, 4);
    private static readonly PropertyKey VideoFrameWidth = new(VideoFmt, 3);
    private static readonly PropertyKey VideoFrameHeight = new(VideoFmt, 4);
    private static readonly PropertyKey VideoFrameRate = new(VideoFmt, 6);
    private static readonly PropertyKey VideoEncodingBitrate = new(VideoFmt, 8);
    private static readonly PropertyKey VideoTotalBitrate = new(VideoFmt, 43);
    private static readonly PropertyKey ImageHorizontalSize = new(ImageFmt, 3);
    private static readonly PropertyKey ImageVerticalSize = new(ImageFmt, 4);
    private static readonly PropertyKey MusicArtist = new(MusicFmt, 2);
    private static readonly PropertyKey MusicAlbumTitle = new(MusicFmt, 4);
    private static readonly PropertyKey MusicAlbumArtist = new(MusicFmt, 13);
    private static readonly PropertyKey Title = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2);
    private static readonly PropertyKey PresentationSlideCount = new("D5CDD502-2E9C-101B-9397-08002B2CF9AE", 7);

    // ── COM interfaces (shobjidl_core.h) ────────────────────────────────────

    private const int SIIGBF_THUMBNAILONLY = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    // Every method up to the ones used is declared, in vtable order (IShellItem's first).
    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);

        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(ref PropertyKey keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PropertyKey key, IntPtr ppropvar);
        [PreserveSig] int GetCLSID(ref PropertyKey key, out Guid pclsid);
        [PreserveSig] int GetFileTime(ref PropertyKey key, out long pft);
        [PreserveSig] int GetInt32(ref PropertyKey key, out int pi);
        [PreserveSig] int GetString(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
        [PreserveSig] int GetUInt32(ref PropertyKey key, out uint pui);
        [PreserveSig] int GetUInt64(ref PropertyKey key, out ulong pull);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    // ── GDI ─────────────────────────────────────────────────────────────────

    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, out NativeBitmap pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BitmapInfoHeader lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
