using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Windows;

/// <summary>
/// Video and audio through Media Foundation's MFPlay API: it builds the whole
/// pipeline from a file name and draws video into a window we give it, with
/// whatever codecs Windows has (H.264, HEVC and AV1 when their extensions are
/// installed, MP3, AAC, FLAC...). Nothing to ship.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationPlayback : IMediaPlayback
{
    private const string ClassName = "GrepdeskVideoSurface";

    // Kept in a field: the window class holds a pointer to it for the life of the process.
    private static readonly WndProc SurfaceProc = SurfaceWndProc;
    private static bool _classRegistered;

    /// <summary>The player drawing into each surface, so the surface's messages can reach it.</summary>
    private static readonly Dictionary<IntPtr, Player> Players = new();

    public IntPtr CreateSurface(IntPtr parent)
    {
        var instance = GetModuleHandleW(null);
        if (!_classRegistered)
        {
            var wc = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(SurfaceProc),
                Instance = instance,
                Cursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                Background = GetStockObject(BLACK_BRUSH),
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref wc) == 0) throw new COMException("RegisterClassEx failed", Marshal.GetHRForLastWin32Error());
            _classRegistered = true;
        }

        var hwnd = CreateWindowExW(0, ClassName, null, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new COMException("CreateWindowEx failed", Marshal.GetHRForLastWin32Error());
        return hwnd;
    }

    public void DestroySurface(IntPtr surface)
    {
        Players.Remove(surface);
        DestroyWindow(surface);
    }

    public IMediaPlayer Open(string path, IntPtr surface)
    {
        var player = new Player(surface);
        Players[surface] = player;
        player.Start(path);
        return player;
    }

    private static IntPtr SurfaceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Players.TryGetValue(hwnd, out var player);
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
                var paint = new PaintStruct();
                var hdc = BeginPaint(hwnd, ref paint);
                if (player is { VideoSize: not null }) player.UpdateVideo();
                else FillRect(hdc, ref paint.Paint, GetStockObject(BLACK_BRUSH));
                EndPaint(hwnd, ref paint);
                return IntPtr.Zero;
            case WM_SIZE:
                player?.UpdateVideo();
                break;
            case WM_LBUTTONUP:
                player?.RaiseClicked();
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private sealed class Player : IMediaPlayer, IMFPMediaPlayerCallback
    {
        private readonly IntPtr _surface;
        private IMFPMediaPlayer? _player;
        private bool _playing;

        public event Action? Ready;
        public event Action<int>? Failed;
        public event Action? StateChanged;
        public event Action? Ended;
        public event Action? SurfaceClicked;

        public Player(IntPtr surface) => _surface = surface;

        public void Start(string path)
        {
            var hr = MFPCreateMediaPlayer(path, true, 0, this, _surface, out _player);
            if (hr < 0) throw new COMException("MFPCreateMediaPlayer failed", hr);
        }

        public bool IsPlaying => _playing;

        public TimeSpan Duration { get; private set; }

        public (int Width, int Height)? VideoSize { get; private set; }

        public TimeSpan Position
        {
            get
            {
                if (_player is null) return TimeSpan.Zero;
                var position = Guid.Empty;
                return _player.GetPosition(ref position, out var value) >= 0 ? value.Ticks : TimeSpan.Zero;
            }
            set
            {
                if (_player is null) return;
                var position = Guid.Empty;
                var pv = new PropVariant { Type = VT_I8, Long = Math.Clamp(value.Ticks, 0, Math.Max(0, Duration.Ticks)) };
                _player.SetPosition(ref position, ref pv);
            }
        }

        public float Volume
        {
            get => _player is not null && _player.GetVolume(out var v) >= 0 ? v : 1f;
            set => _player?.SetVolume(Math.Clamp(value, 0f, 1f));
        }

        public bool IsMuted
        {
            get => _player is not null && _player.GetMute(out var m) >= 0 && m;
            set => _player?.SetMute(value);
        }

        public void Play() => _player?.Play();

        public void Pause() => _player?.Pause();

        public void UpdateVideo() => _player?.UpdateVideo();

        internal void RaiseClicked() => SurfaceClicked?.Invoke();

        void IMFPMediaPlayerCallback.OnMediaPlayerEvent(IntPtr header)
        {
            if (_player is null) return;
            var type = Marshal.ReadInt32(header);
            var hr = Marshal.ReadInt32(header, 4);

            if (hr < 0 || type == MFP_EVENT_TYPE_ERROR)
            {
                _playing = false;
                Failed?.Invoke(hr);
                return;
            }

            switch (type)
            {
                case MFP_EVENT_TYPE_MEDIAITEM_SET:
                    var position = Guid.Empty;
                    if (_player.GetDuration(ref position, out var duration) >= 0)
                        Duration = duration.Ticks;
                    VideoSize = _player.GetNativeVideoSize(out var size, out _) >= 0 && size.Width > 0 && size.Height > 0
                        ? (size.Width, size.Height)
                        : null;
                    Ready?.Invoke();
                    break;
                case MFP_EVENT_TYPE_PLAY:
                    _playing = true;
                    StateChanged?.Invoke();
                    break;
                case MFP_EVENT_TYPE_PAUSE:
                case MFP_EVENT_TYPE_STOP:
                    _playing = false;
                    StateChanged?.Invoke();
                    break;
                case MFP_EVENT_TYPE_PLAYBACK_ENDED:
                    _playing = false;
                    StateChanged?.Invoke();
                    Ended?.Invoke();
                    break;
            }
        }

        public void Dispose()
        {
            if (_player is null) return;
            var player = _player;
            _player = null;
            Players.Remove(_surface);
            player.Shutdown();
            Marshal.ReleaseComObject(player);
        }
    }

    // ---------------------------------------------------------------------
    // MFPlay (mfplay.h)
    // ---------------------------------------------------------------------

    private const int MFP_EVENT_TYPE_PLAY = 0;
    private const int MFP_EVENT_TYPE_PAUSE = 1;
    private const int MFP_EVENT_TYPE_STOP = 2;
    private const int MFP_EVENT_TYPE_MEDIAITEM_SET = 6;
    private const int MFP_EVENT_TYPE_ERROR = 10;
    private const int MFP_EVENT_TYPE_PLAYBACK_ENDED = 11;

    private const ushort VT_I8 = 20, VT_UI8 = 21;

    [DllImport("mfplay.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFPCreateMediaPlayer(string url, [MarshalAs(UnmanagedType.Bool)] bool startPlayback, int options,
        IMFPMediaPlayerCallback callback, IntPtr hwnd, out IMFPMediaPlayer player);

    /// <summary>
    /// PROPVARIANT holding a time in 100 ns units: positions come back as
    /// VT_I8, the duration as VT_UI8. 24 bytes on x64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Long;

        public readonly TimeSpan Ticks => Type is VT_I8 or VT_UI8 && Long > 0 ? TimeSpan.FromTicks(Long) : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport, Guid("766C8FFB-5FDB-4fea-A28D-B912996F51BD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFPMediaPlayerCallback
    {
        [PreserveSig] void OnMediaPlayerEvent(IntPtr eventHeader);
    }

    // Every method, in vtable order, even the unused ones.
    [ComImport, Guid("A714590A-58AF-430a-85BF-44F5EC838D85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFPMediaPlayer
    {
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int FrameStep();
        [PreserveSig] int SetPosition(ref Guid positionType, ref PropVariant position);
        [PreserveSig] int GetPosition(ref Guid positionType, out PropVariant position);
        [PreserveSig] int GetDuration(ref Guid positionType, out PropVariant duration);
        [PreserveSig] int SetRate(float rate);
        [PreserveSig] int GetRate(out float rate);
        [PreserveSig] int GetSupportedRates([MarshalAs(UnmanagedType.Bool)] bool forward, out float slowest, out float fastest);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int CreateMediaItemFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.Bool)] bool sync, IntPtr userData, out IntPtr item);
        [PreserveSig] int CreateMediaItemFromObject(IntPtr obj, [MarshalAs(UnmanagedType.Bool)] bool sync, IntPtr userData, out IntPtr item);
        [PreserveSig] int SetMediaItem(IntPtr item);
        [PreserveSig] int ClearMediaItem();
        [PreserveSig] int GetMediaItem(out IntPtr item);
        [PreserveSig] int GetVolume(out float volume);
        [PreserveSig] int SetVolume(float volume);
        [PreserveSig] int GetBalance(out float balance);
        [PreserveSig] int SetBalance(float balance);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute);
        [PreserveSig] int GetNativeVideoSize(out NativeSize video, out NativeSize aspectRatio);
        [PreserveSig] int GetIdealVideoSize(out NativeSize min, out NativeSize max);
        [PreserveSig] int SetVideoSourceRect(IntPtr rect);
        [PreserveSig] int GetVideoSourceRect(IntPtr rect);
        [PreserveSig] int SetAspectRatioMode(int mode);
        [PreserveSig] int GetAspectRatioMode(out int mode);
        [PreserveSig] int GetVideoWindow(out IntPtr hwnd);
        [PreserveSig] int UpdateVideo();
        [PreserveSig] int SetBorderColor(uint color);
        [PreserveSig] int GetBorderColor(out uint color);
        [PreserveSig] int InsertEffect(IntPtr effect, [MarshalAs(UnmanagedType.Bool)] bool optional);
        [PreserveSig] int RemoveEffect(IntPtr effect);
        [PreserveSig] int RemoveAllEffects();
        [PreserveSig] int Shutdown();
    }

    // ---------------------------------------------------------------------
    // Win32 window for the video
    // ---------------------------------------------------------------------

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const uint WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_LBUTTONUP = 0x0202;
    private const int BLACK_BRUSH = 4;
    private static readonly IntPtr IDC_ARROW = 32512;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
