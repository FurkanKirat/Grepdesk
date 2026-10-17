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
/// <para>
/// MFPlay only plays a file's first audio track. When there are several, we
/// select them all and give each its own audio renderer (Windows mixes them),
/// so each can be silenced through its own stream volume.
/// </para>
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

        /// <summary>One per audio track when there are several, each playing into its own renderer.</summary>
        private readonly List<Track> _tracks = new();
        private AudioTrack[] _audioTracks = [];

        private sealed record Track(IMFMediaSink Sink)
        {
            public bool Enabled { get; set; } = true;
        }

        public Player(IntPtr surface) => _surface = surface;

        /// <summary>The item is created first and set by hand, so its streams can be set up before playback.</summary>
        public void Start(string path)
        {
            var hr = MFPCreateMediaPlayer(null, false, 0, this, _surface, out _player);
            if (hr < 0) throw new COMException("MFPCreateMediaPlayer failed", hr);
            hr = _player.CreateMediaItemFromURL(path, false, IntPtr.Zero, IntPtr.Zero);
            if (hr < 0) throw new COMException("CreateMediaItemFromURL failed", hr);
        }

        public IReadOnlyList<AudioTrack> AudioTracks => _audioTracks;

        public bool IsTrackEnabled(int track) => track >= 0 && track < _tracks.Count && _tracks[track].Enabled;

        public void SetTrackEnabled(int track, bool enabled)
        {
            if (track < 0 || track >= _tracks.Count) return;
            _tracks[track].Enabled = enabled;
            ApplyTrackVolume(_tracks[track]);
        }

        /// <summary>
        /// The renderer's stream volume only exists once its format is known, so
        /// this is done again when playback starts.
        /// </summary>
        private static void ApplyTrackVolume(Track track)
        {
            if (track.Sink is not IMFGetService service) return;
            var serviceId = MR_STREAM_VOLUME_SERVICE;
            var iid = typeof(IMFAudioStreamVolume).GUID;
            if (service.GetService(ref serviceId, ref iid, out var pointer) < 0 || pointer == IntPtr.Zero) return;
            try
            {
                var volume = (IMFAudioStreamVolume)Marshal.GetObjectForIUnknown(pointer);
                if (volume.GetChannelCount(out var channels) >= 0)
                    for (uint c = 0; c < channels; c++)
                        volume.SetChannelVolume(c, track.Enabled ? 1f : 0f);
                Marshal.ReleaseComObject(volume);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        /// <summary>
        /// Selects every audio stream and gives each a renderer of its own. On
        /// any failure the item is put back as MFPlay chose it: first track only.
        /// </summary>
        private void SetUpAudioTracks(IMFPMediaItem item)
        {
            if (item.GetNumberOfStreams(out var count) < 0) return;
            var audio = new List<uint>();
            for (uint i = 0; i < count; i++)
                if (StreamGuid(item, i, MF_MT_MAJOR_TYPE) == MFMediaType_Audio) audio.Add(i);
            if (audio.Count < 2) return;

            var selected = new bool[audio.Count];
            for (var i = 0; i < audio.Count; i++)
                selected[i] = item.GetStreamSelection(audio[i], out var on) >= 0 && on;

            var tracks = new List<AudioTrack>();
            try
            {
                foreach (var stream in audio)
                {
                    var hr = MFCreateAudioRenderer(IntPtr.Zero, out var sink);
                    if (hr < 0) throw new COMException("MFCreateAudioRenderer failed", hr);
                    _tracks.Add(new Track(sink));
                    hr = sink.GetStreamSinkByIndex(0, out var streamSink);
                    if (hr < 0) throw new COMException("GetStreamSinkByIndex failed", hr);
                    try
                    {
                        hr = item.SetStreamSelection(stream, true);
                        if (hr >= 0) hr = item.SetStreamSink(stream, streamSink);
                        if (hr < 0) throw new COMException("SetStreamSink failed", hr);
                    }
                    finally
                    {
                        Marshal.Release(streamSink);
                    }
                    tracks.Add(new AudioTrack(StreamString(item, stream, MF_SD_STREAM_NAME),
                        StreamString(item, stream, MF_SD_LANGUAGE), (int)StreamUInt(item, stream, MF_MT_AUDIO_NUM_CHANNELS)));
                }
                _audioTracks = tracks.ToArray();
            }
            catch (COMException)
            {
                for (var i = 0; i < audio.Count; i++)
                {
                    item.SetStreamSink(audio[i], IntPtr.Zero);
                    item.SetStreamSelection(audio[i], selected[i]);
                }
                ShutDownTracks();
            }
        }

        private void ShutDownTracks()
        {
            foreach (var track in _tracks)
            {
                track.Sink.Shutdown();
                Marshal.ReleaseComObject(track.Sink);
            }
            _tracks.Clear();
            _audioTracks = [];
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
                case MFP_EVENT_TYPE_MEDIAITEM_CREATED:
                    var itemPointer = Marshal.ReadIntPtr(header, MediaItemOffset);
                    var item = (IMFPMediaItem)Marshal.GetObjectForIUnknown(itemPointer);
                    try
                    {
                        SetUpAudioTracks(item);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                    hr = _player.SetMediaItem(itemPointer);
                    if (hr < 0) Failed?.Invoke(hr);
                    break;
                case MFP_EVENT_TYPE_MEDIAITEM_SET:
                    var position = Guid.Empty;
                    if (_player.GetDuration(ref position, out var duration) >= 0)
                        Duration = duration.Ticks;
                    VideoSize = _player.GetNativeVideoSize(out var size, out _) >= 0 && size.Width > 0 && size.Height > 0
                        ? (size.Width, size.Height)
                        : null;
                    Ready?.Invoke();
                    _player?.Play();
                    break;
                case MFP_EVENT_TYPE_PLAY:
                    foreach (var track in _tracks) ApplyTrackVolume(track);
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
            ShutDownTracks();
        }

        private static Guid StreamGuid(IMFPMediaItem item, uint stream, Guid attribute)
        {
            if (item.GetStreamAttribute(stream, ref attribute, out var value) < 0) return Guid.Empty;
            var result = value.Type == VT_CLSID && value.Pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(value.Pointer) : Guid.Empty;
            PropVariantClear(ref value);
            return result;
        }

        private static string? StreamString(IMFPMediaItem item, uint stream, Guid attribute)
        {
            if (item.GetStreamAttribute(stream, ref attribute, out var value) < 0) return null;
            var result = value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null;
            PropVariantClear(ref value);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }

        private static uint StreamUInt(IMFPMediaItem item, uint stream, Guid attribute)
        {
            if (item.GetStreamAttribute(stream, ref attribute, out var value) < 0) return 0;
            var result = value.Type == VT_UI4 ? value.UInt : 0;
            PropVariantClear(ref value);
            return result;
        }
    }

    // ---------------------------------------------------------------------
    // MFPlay (mfplay.h)
    // ---------------------------------------------------------------------

    private const int MFP_EVENT_TYPE_PLAY = 0;
    private const int MFP_EVENT_TYPE_PAUSE = 1;
    private const int MFP_EVENT_TYPE_STOP = 2;
    private const int MFP_EVENT_TYPE_MEDIAITEM_CREATED = 5;
    private const int MFP_EVENT_TYPE_MEDIAITEM_SET = 6;
    private const int MFP_EVENT_TYPE_ERROR = 10;
    private const int MFP_EVENT_TYPE_PLAYBACK_ENDED = 11;

    private const ushort VT_UI4 = 19, VT_I8 = 20, VT_UI8 = 21, VT_LPWSTR = 31, VT_CLSID = 72;

    /// <summary>
    /// Where MFP_MEDIAITEM_CREATED_EVENT keeps its item: after the
    /// MFP_EVENT_HEADER (type, HRESULT, player, state, property store).
    /// </summary>
    private static readonly int MediaItemOffset = IntPtr.Size == 8 ? 32 : 20;

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_SD_LANGUAGE = new("00af2180-bdc2-423c-abca-f503593bc121");
    private static readonly Guid MF_SD_STREAM_NAME = new("4f1b099d-d314-41e5-a781-7fefaa4c501f");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MR_STREAM_VOLUME_SERVICE = new("f8b5fa2f-32ef-46f5-b172-1321212fb2c4");

    [DllImport("mfplay.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFPCreateMediaPlayer(string? url, [MarshalAs(UnmanagedType.Bool)] bool startPlayback, int options,
        IMFPMediaPlayerCallback callback, IntPtr hwnd, out IMFPMediaPlayer player);

    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFCreateAudioRenderer(IntPtr attributes, out IMFMediaSink sink);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    /// <summary>
    /// PROPVARIANT holding a time in 100 ns units (positions come back as
    /// VT_I8, the duration as VT_UI8), or a stream attribute. 24 bytes on x64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Long;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public IntPtr Pointer;

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
        // The item pointer must be null when not synchronous: the item arrives with MFP_EVENT_TYPE_MEDIAITEM_CREATED.
        [PreserveSig] int CreateMediaItemFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.Bool)] bool sync, IntPtr userData, IntPtr item);
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

    // Every method, in vtable order, even the unused ones.
    [ComImport, Guid("90EB3E6B-ECBF-45cc-B1DA-C6FE3EA70D57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFPMediaItem
    {
        [PreserveSig] int GetMediaPlayer(out IntPtr player);
        [PreserveSig] int GetURL(out IntPtr url);
        [PreserveSig] int GetObject(out IntPtr obj);
        [PreserveSig] int GetUserData(out IntPtr userData);
        [PreserveSig] int SetUserData(IntPtr userData);
        [PreserveSig] int GetStartStopPosition(IntPtr startFormat, IntPtr start, IntPtr stopFormat, IntPtr stop);
        [PreserveSig] int SetStartStopPosition(IntPtr startFormat, IntPtr start, IntPtr stopFormat, IntPtr stop);
        [PreserveSig] int HasVideo([MarshalAs(UnmanagedType.Bool)] out bool hasVideo, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int HasAudio([MarshalAs(UnmanagedType.Bool)] out bool hasAudio, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int IsProtected([MarshalAs(UnmanagedType.Bool)] out bool isProtected);
        [PreserveSig] int GetDuration(ref Guid positionType, out PropVariant duration);
        [PreserveSig] int GetNumberOfStreams(out uint count);
        [PreserveSig] int GetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool enabled);
        [PreserveSig] int GetStreamAttribute(uint stream, ref Guid attribute, out PropVariant value);
        [PreserveSig] int GetPresentationAttribute(ref Guid attribute, out PropVariant value);
        [PreserveSig] int GetCharacteristics(out int characteristics);
        [PreserveSig] int SetStreamSink(uint stream, IntPtr sink);
        [PreserveSig] int GetMetadata(out IntPtr propertyStore);
    }

    // ---------------------------------------------------------------------
    // The audio renderer and its per-stream volume (mfidl.h)
    // ---------------------------------------------------------------------

    [ComImport, Guid("6ef2a660-47c0-4666-b13d-cbb717f2fa2c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaSink
    {
        [PreserveSig] int GetCharacteristics(out uint characteristics);
        [PreserveSig] int AddStreamSink(uint id, IntPtr mediaType, out IntPtr streamSink);
        [PreserveSig] int RemoveStreamSink(uint id);
        [PreserveSig] int GetStreamSinkCount(out uint count);
        [PreserveSig] int GetStreamSinkByIndex(uint index, out IntPtr streamSink);
        [PreserveSig] int GetStreamSinkById(uint id, out IntPtr streamSink);
        [PreserveSig] int SetPresentationClock(IntPtr clock);
        [PreserveSig] int GetPresentationClock(out IntPtr clock);
        [PreserveSig] int Shutdown();
    }

    [ComImport, Guid("fa993888-4383-415a-a930-dd472a8cf6f7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFGetService
    {
        [PreserveSig] int GetService(ref Guid service, ref Guid iid, out IntPtr obj);
    }

    [ComImport, Guid("76B1BBDB-4EC8-4f36-B106-70A9316DF593"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAudioStreamVolume
    {
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetChannelVolume(uint channel, float level);
        [PreserveSig] int GetChannelVolume(uint channel, out float level);
        [PreserveSig] int SetAllVolumes(uint count, [In] float[] levels);
        [PreserveSig] int GetAllVolumes(uint count, [Out] float[] levels);
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
