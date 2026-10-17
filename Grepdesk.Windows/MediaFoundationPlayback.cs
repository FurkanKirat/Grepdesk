using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Windows;

/// <summary>
/// Video and audio through Media Foundation's MFPlay API: it builds the whole
/// pipeline from a file name, with whatever codecs Windows has (H.264, HEVC
/// and AV1 when their extensions are installed, MP3, AAC, FLAC...). Nothing to ship.
/// <para>
/// MFPlay only plays a file's first audio track. When there are several, we
/// select them all and give each its own audio renderer (Windows mixes them),
/// so each can be silenced through its own stream volume.
/// </para>
/// <para>
/// Video doesn't go to MFPlay's own renderer, the EVR: it is built on
/// Direct3D 9 and crashed inside D3D9.DLL on some graphics drivers. The video
/// stream goes to a sample grabber instead, which hands us each decoded frame
/// as RGB at its presentation time, and we draw it with GDI. Decoding is in
/// software, and nothing touches the graphics driver beyond a GDI blit.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationPlayback : IMediaPlayback
{
    private const string ClassName = "GrepdeskVideoSurface";

    // Kept in a field: the window class holds a pointer to it for the life of the process.
    private static readonly WndProc SurfaceProc = SurfaceWndProc;
    private static bool _classRegistered;

    /// <summary>The player drawing into each surface, so the surface's messages can reach it. UI thread only.</summary>
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

    /// <summary>The player goes first: it draws into the window from Media Foundation's threads.</summary>
    public void DestroySurface(IntPtr surface)
    {
        if (Players.Remove(surface, out var player)) player.Dispose();
        DestroyWindow(surface);
    }

    public IMediaPlayer Open(string path, IntPtr surface)
    {
        var player = new Player(surface);
        try
        {
            player.Start(path);
        }
        catch
        {
            player.Dispose();
            throw;
        }
        if (surface != IntPtr.Zero) Players[surface] = player;
        return player;
    }

    /// <summary>
    /// Called by Windows: an exception must not travel back into native code,
    /// where it ends the process without a word. It stops playback instead.
    /// </summary>
    private static IntPtr SurfaceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Players.TryGetValue(hwnd, out var player);
        try
        {
            if (HandleSurfaceMessage(hwnd, msg, player) is { } result) return result;
        }
        catch (Exception e)
        {
            player?.Fail(new MediaError(e.HResult, $"video window message 0x{msg:X4}", e));
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <returns>The message's result, or null to leave it to the default handling.</returns>
    private static IntPtr? HandleSurfaceMessage(IntPtr hwnd, uint msg, Player? player)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
                var paint = new PaintStruct();
                var hdc = BeginPaint(hwnd, ref paint);
                try
                {
                    if (player is null || !player.DrawFrame(hdc)) FillRect(hdc, ref paint.Paint, GetStockObject(BLACK_BRUSH));
                }
                finally
                {
                    EndPaint(hwnd, ref paint);
                }
                return IntPtr.Zero;
            case WM_SIZE:
                player?.UpdateVideo();
                break;
            case WM_LBUTTONUP:
                player?.RaiseClicked();
                return IntPtr.Zero;
        }
        return null;
    }

    private sealed class Player : IMediaPlayer, IMFPMediaPlayerCallback
    {
        private readonly IntPtr _surface;
        private IMFPMediaPlayer? _player;
        private bool _playing;

        public event Action? Ready;
        public event Action<MediaError>? Failed;
        public event Action? StateChanged;
        public event Action? Ended;
        public event Action? SurfaceClicked;
        public event Action<string>? Step;

        /// <summary>One per audio track when there are several, each playing into its own renderer.</summary>
        private readonly List<Track> _tracks = new();
        private AudioTrack[] _audioTracks = [];

        private sealed record Track(IMFMediaSink Sink)
        {
            public bool Enabled { get; set; } = true;
        }

        /// <summary>Where video frames arrive and are drawn; null for audio, or until the item is read.</summary>
        private VideoFrames? _video;
        private IMFMediaSink? _videoSink;

        /// <summary>The UI thread's, for what the grabber reports from Media Foundation's threads.</summary>
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public Player(IntPtr surface) => _surface = surface;

        /// <summary>
        /// The item is created first and set by hand, so its streams can be set up before playback.
        /// No window for MFPlay: without one it makes no video renderer of its own.
        /// </summary>
        public void Start(string path)
        {
            var hr = MFPCreateMediaPlayer(null, false, 0, this, IntPtr.Zero, out _player);
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

        public (int Width, int Height)? VideoSize => _video is { } v ? (v.Width, v.Height) : null;

        /// <summary>
        /// Sends the first video stream to a sample grabber that wants NV12, what
        /// decoders put out, so nothing sits between them. With a converter in
        /// between (asking for RGB), playback failed with MF_E_ATTRIBUTENOTFOUND
        /// whenever the decoder changed its output on the first frame, as H.264
        /// does for any size not a multiple of 16 (1080 lines decode as 1088 and
        /// a crop). The size is left open; the grabber reads what it got.
        /// </summary>
        private void SetUpVideo(IMFPMediaItem item, uint count)
        {
            if (_surface == IntPtr.Zero) return;
            for (uint i = 0; i < count; i++)
            {
                if (StreamGuid(item, i, MF_MT_MAJOR_TYPE) != MFMediaType_Video) continue;
                var (width, height) = Split(StreamULong(item, i, MF_MT_FRAME_SIZE));
                if (width == 0 || height == 0) continue;
                var (pixelX, pixelY) = Split(StreamULong(item, i, MF_MT_PIXEL_ASPECT_RATIO));
                var aspect = width * (pixelX > 0 && pixelY > 0 ? (double)pixelX / pixelY : 1) / height;
                var video = new VideoFrames(_surface, (int)width, (int)height, aspect, () => Post(() => Step?.Invoke("first frame shown")));

                var hr = MFCreateMediaType(out var typePointer);
                if (hr < 0) throw new COMException("MFCreateMediaType failed", hr);
                var activate = IntPtr.Zero;
                try
                {
                    var type = (IMFAttributes)Marshal.GetObjectForIUnknown(typePointer);
                    type.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                    type.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                    Marshal.ReleaseComObject(type);

                    hr = MFCreateSampleGrabberSinkActivate(typePointer, video, out activate);
                    if (hr < 0) throw new COMException("MFCreateSampleGrabberSinkActivate failed", hr);
                    hr = Vtable.ActivateObject(activate, typeof(IMFMediaSink).GUID, out var sinkPointer);
                    if (hr < 0) throw new COMException("Activating the sample grabber failed", hr);
                    _videoSink = (IMFMediaSink)Marshal.GetObjectForIUnknown(sinkPointer);
                    Marshal.Release(sinkPointer);

                    hr = _videoSink.GetStreamSinkByIndex(0, out var streamSink);
                    if (hr < 0) throw new COMException("GetStreamSinkByIndex (video) failed", hr);
                    video.StreamSink = streamSink;
                    hr = item.SetStreamSelection(i, true);
                    if (hr >= 0) hr = item.SetStreamSink(i, streamSink);
                    if (hr < 0)
                    {
                        video.Stop();
                        throw new COMException("SetStreamSink (video) failed", hr);
                    }
                    _video = video;
                }
                finally
                {
                    if (activate != IntPtr.Zero) Marshal.Release(activate);
                    Marshal.Release(typePointer);
                }
                return;
            }
        }

        /// <summary>From Media Foundation's threads to the one that created the player.</summary>
        private void Post(Action action)
        {
            if (_context is null) action();
            else _context.Post(_ => action(), null);
        }

        /// <summary>Draws the latest frame for WM_PAINT; false when there is none yet.</summary>
        internal bool DrawFrame(IntPtr hdc) => _video?.Draw(hdc) == true;

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

        public void UpdateVideo() => _video?.Redraw();

        internal void RaiseClicked() => SurfaceClicked?.Invoke();

        /// <summary>Stops reporting progress and tells the UI once; it disposes the player in response.</summary>
        internal void Fail(MediaError error)
        {
            _playing = false;
            var failed = Failed;
            Failed = null;
            failed?.Invoke(error);
        }

        /// <summary>
        /// Called by Media Foundation through a [PreserveSig] void method: an
        /// exception escaping it would end the process with no log, so it is
        /// turned into a playback failure.
        /// </summary>
        void IMFPMediaPlayerCallback.OnMediaPlayerEvent(IntPtr header)
        {
            try
            {
                OnEvent(header);
            }
            catch (Exception e)
            {
                Fail(new MediaError(e.HResult, "playback event", e));
            }
        }

        private void OnEvent(IntPtr header)
        {
            if (_player is null) return;
            var type = Marshal.ReadInt32(header);
            var hr = Marshal.ReadInt32(header, 4);

            if (hr < 0 || type == MFP_EVENT_TYPE_ERROR)
            {
                Fail(new MediaError(hr, type == MFP_EVENT_TYPE_ERROR ? "playback" : $"playback event {type}"));
                return;
            }

            switch (type)
            {
                case MFP_EVENT_TYPE_MEDIAITEM_CREATED:
                    var itemPointer = Marshal.ReadIntPtr(header, MediaItemOffset);
                    var item = (IMFPMediaItem)Marshal.GetObjectForIUnknown(itemPointer);
                    try
                    {
                        var streams = item.GetNumberOfStreams(out var count) >= 0 ? count : 0;
                        Step?.Invoke($"file read, {streams} streams");
                        SetUpVideo(item, streams);
                        SetUpAudioTracks(item);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                    // Picks the decoders and converters, and opens the audio device.
                    Step?.Invoke($"building the pipeline{(_video is { } v ? $", video {v.Width}×{v.Height} to the sample grabber" : "")}"
                        + (_tracks.Count > 0 ? $", {_tracks.Count} audio tracks" : ""));
                    hr = _player.SetMediaItem(itemPointer);
                    if (hr < 0) Fail(new MediaError(hr, "set media item"));
                    break;
                case MFP_EVENT_TYPE_MEDIAITEM_SET:
                    var position = Guid.Empty;
                    if (_player.GetDuration(ref position, out var duration) >= 0)
                        Duration = duration.Ticks;
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
            if (Players.TryGetValue(_surface, out var registered) && registered == this) Players.Remove(_surface);
            // Before the shutdown: a frame may be arriving on another thread, and the window is next to go.
            _video?.Stop();
            if (_player is not null)
            {
                var player = _player;
                _player = null;
                player.Shutdown();
                Marshal.ReleaseComObject(player);
            }
            ShutDownTracks();
            if (_videoSink is not null)
            {
                _videoSink.Shutdown();
                Marshal.ReleaseComObject(_videoSink);
                _videoSink = null;
            }
        }

        private static ulong StreamULong(IMFPMediaItem item, uint stream, Guid attribute)
        {
            if (item.GetStreamAttribute(stream, ref attribute, out var value) < 0) return 0;
            var result = value.Type is VT_UI8 or VT_I8 ? (ulong)value.Long : 0;
            PropVariantClear(ref value);
            return result;
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

    /// <summary>Frame sizes and ratios are two UINT32s packed into one UINT64, the first on top.</summary>
    private static (uint High, uint Low) Split(ulong value) => ((uint)(value >> 32), (uint)value);

    /// <summary>
    /// The sample grabber's callback: converts the latest frame to RGB and
    /// draws it, letterboxed, into the video window. Frames arrive on Media
    /// Foundation's threads at their presentation time, repaints and resizes
    /// come from the UI thread; the lock keeps them apart, and away from a
    /// stopped player.
    /// </summary>
    private sealed class VideoFrames : IMFSampleGrabberSinkCallback
    {
        private readonly IntPtr _surface;
        private readonly double _aspect;
        private readonly Action _firstFrame;
        private readonly object _lock = new();
        private byte[] _pixels = [];
        private BitmapInfoHeader _header;
        private Format? _format;
        private bool _hasFrame;
        private bool _stopped;

        /// <summary>
        /// What the decoder sends: NV12 at a size rounded up to its blocks (1080
        /// lines arrive as 1088), the part to show, and how to turn it into RGB.
        /// </summary>
        private sealed record Format(int Height, int Stride, Rect Visible, YuvToRgb Colors)
        {
            /// <summary>The luma plane, then half as much interleaved chroma.</summary>
            public int Bytes => Stride * Height * 3 / 2;
            public int VisibleWidth => Visible.Right - Visible.Left;
            public int VisibleHeight => Visible.Bottom - Visible.Top;
        }

        /// <summary>The picture's size as the file describes it.</summary>
        public int Width { get; }
        public int Height { get; }

        /// <summary>The grabber's IMFStreamSink, for the media type frames arrive in. Released by <see cref="Stop"/>.</summary>
        public IntPtr StreamSink { get; set; }

        /// <param name="aspect">The picture's width over its height, as displayed.</param>
        /// <param name="firstFrame">Called once, from a Media Foundation thread.</param>
        public VideoFrames(IntPtr surface, int width, int height, double aspect, Action firstFrame)
        {
            _surface = surface;
            Width = width;
            Height = height;
            _aspect = aspect;
            _firstFrame = firstFrame;
        }

        public void Stop()
        {
            lock (_lock)
            {
                _stopped = true;
                if (StreamSink != IntPtr.Zero) Marshal.Release(StreamSink);
                StreamSink = IntPtr.Zero;
            }
        }

        public void Redraw()
        {
            lock (_lock)
            {
                if (_stopped || !_hasFrame) return;
                DrawToWindow();
            }
        }

        /// <summary>Into a DC from BeginPaint; false when there is no frame to draw.</summary>
        public bool Draw(IntPtr hdc)
        {
            lock (_lock)
            {
                if (_stopped || !_hasFrame) return false;
                DrawLocked(hdc);
                return true;
            }
        }

        private void DrawToWindow()
        {
            var hdc = GetDC(_surface);
            if (hdc == IntPtr.Zero) return;
            try
            {
                DrawLocked(hdc);
            }
            finally
            {
                ReleaseDC(_surface, hdc);
            }
        }

        /// <summary>As large as fits in the picture's own proportions, black around it.</summary>
        private void DrawLocked(IntPtr hdc)
        {
            if (_format is not { } format) return;
            GetClientRect(_surface, out var client);
            int cw = client.Right, ch = client.Bottom;
            if (cw <= 0 || ch <= 0) return;
            var scale = Math.Min(cw / (Height * _aspect), (double)ch / Height);
            var w = Math.Max(1, (int)Math.Round(Height * _aspect * scale));
            var h = Math.Max(1, (int)Math.Round(Height * scale));
            int x = (cw - w) / 2, y = (ch - h) / 2;

            var black = GetStockObject(BLACK_BRUSH);
            var bars = new[]
            {
                new Rect { Left = 0, Top = 0, Right = cw, Bottom = y },
                new Rect { Left = 0, Top = y + h, Right = cw, Bottom = ch },
                new Rect { Left = 0, Top = y, Right = x, Bottom = y + h },
                new Rect { Left = x + w, Top = y, Right = cw, Bottom = y + h },
            };
            foreach (var bar in bars)
            {
                var rect = bar;
                if (rect.Right > rect.Left && rect.Bottom > rect.Top) FillRect(hdc, ref rect, black);
            }

            // HALFTONE averages the pixels a smaller picture skips; it needs the brush origin set after it.
            SetStretchBltMode(hdc, HALFTONE);
            SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
            StretchDIBits(hdc, x, y, w, h, 0, 0, format.VisibleWidth, format.VisibleHeight, _pixels, ref _header, DIB_RGB_COLORS, SRCCOPY);
        }

        /// <summary>The media type the grabber receives now; its aperture is the part to show.</summary>
        private Format? ReadFormat()
        {
            if (StreamSink == IntPtr.Zero || Vtable.GetCurrentMediaType(StreamSink, out var type) < 0) return null;
            try
            {
                if (Vtable.GetUInt64(type, MF_MT_FRAME_SIZE, out var size) < 0) return null;
                var (width, height) = Split(size);
                if (width < 2 || height < 2) return null;
                var stride = Vtable.GetUInt32(type, MF_MT_DEFAULT_STRIDE, out var s) >= 0 && (int)s >= width ? (int)s : (int)width;

                var full = new Rect { Right = (int)width, Bottom = (int)height };
                var visible = Vtable.GetArea(type, MF_MT_MINIMUM_DISPLAY_APERTURE)
                    ?? Vtable.GetArea(type, MF_MT_GEOMETRIC_APERTURE)
                    ?? full;
                visible.Left = Math.Clamp(visible.Left, 0, full.Right - 1);
                visible.Top = Math.Clamp(visible.Top, 0, full.Bottom - 1);
                visible.Right = Math.Clamp(visible.Right, visible.Left + 1, full.Right);
                visible.Bottom = Math.Clamp(visible.Bottom, visible.Top + 1, full.Bottom);

                var matrix = Vtable.GetUInt32(type, MF_MT_YUV_MATRIX, out var m) >= 0 ? m : 0;
                var fullRange = Vtable.GetUInt32(type, MF_MT_VIDEO_NOMINAL_RANGE, out var r) >= 0 && r == MFNominalRange_0_255;
                return new Format((int)height, stride, visible, YuvToRgb.For(matrix, height >= 720, fullRange));
            }
            finally
            {
                Marshal.Release(type);
            }
        }

        /// <summary>
        /// Called by Media Foundation: an exception escaping into it would end
        /// the process with no log. A frame that can't be drawn is skipped.
        /// </summary>
        int IMFSampleGrabberSinkCallback.OnProcessSample(in Guid majorMediaType, uint sampleFlags, long sampleTime, long sampleDuration,
            IntPtr sampleBuffer, uint sampleSize)
        {
            try
            {
                bool first;
                lock (_lock)
                {
                    if (_stopped) return 0;
                    if (_format?.Bytes != sampleSize)
                    {
                        _format = ReadFormat();
                        if (_format is not { } format || format.Bytes > sampleSize) return 0;
                        _pixels = new byte[format.VisibleWidth * format.VisibleHeight * 4];
                        // A negative height is a top-down DIB.
                        _header = new BitmapInfoHeader
                        {
                            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                            Width = format.VisibleWidth,
                            Height = -format.VisibleHeight,
                            Planes = 1,
                            BitCount = 32,
                        };
                    }
                    ConvertNv12(sampleBuffer, _format, _pixels);
                    first = !_hasFrame;
                    _hasFrame = true;
                    DrawToWindow();
                }
                if (first) _firstFrame();
            }
            catch
            {
                // The next frame tries again.
            }
            return 0;
        }

        /// <summary>The visible part of an NV12 frame as top-down BGRA rows; large frames are split across cores.</summary>
        private static void ConvertNv12(IntPtr source, Format format, byte[] target)
        {
            var rows = format.VisibleHeight;
            var bands = rows >= 480 ? Math.Min(Environment.ProcessorCount, 8) : 1;
            if (bands == 1)
            {
                ConvertRows(source, format, target, 0, rows);
                return;
            }
            // Bands of whole row pairs, so no two share a chroma row.
            var bandRows = (rows / bands + 1) & ~1;
            Parallel.For(0, bands, band =>
            {
                var first = band * bandRows;
                if (first < rows) ConvertRows(source, format, target, first, Math.Min(rows, first + bandRows));
            });
        }

        private static unsafe void ConvertRows(IntPtr source, Format format, byte[] target, int firstRow, int endRow)
        {
            var c = format.Colors;
            var visible = format.Visible;
            var width = format.VisibleWidth;
            var luma = (byte*)source;
            var chroma = luma + format.Stride * format.Height;
            fixed (byte* output = target)
            {
                for (var row = firstRow; row < endRow; row++)
                {
                    var y = visible.Top + row;
                    var yRow = luma + y * format.Stride + visible.Left;
                    var uvRow = chroma + (y >> 1) * format.Stride;
                    var o = output + row * width * 4;
                    for (var i = 0; i < width; i++)
                    {
                        var x = visible.Left + i;
                        var uv = uvRow + (x & ~1);
                        var l = (yRow[i] - c.LumaOffset) * c.Luma;
                        int u = uv[0] - 128, v = uv[1] - 128;
                        o[0] = Clamp(l + c.BlueU * u);
                        o[1] = Clamp(l - c.GreenU * u - c.GreenV * v);
                        o[2] = Clamp(l + c.RedV * v);
                        o[3] = 255;
                        o += 4;
                    }
                }
            }
        }

        private static byte Clamp(int value) => (byte)Math.Clamp((value + 32768) >> 16, 0, 255);

        int IMFSampleGrabberSinkCallback.OnClockStart(long systemTime, long clockStartOffset) => 0;
        int IMFSampleGrabberSinkCallback.OnClockStop(long systemTime) => 0;
        int IMFSampleGrabberSinkCallback.OnClockPause(long systemTime) => 0;
        int IMFSampleGrabberSinkCallback.OnClockRestart(long systemTime) => 0;
        int IMFSampleGrabberSinkCallback.OnClockSetRate(long systemTime, float rate) => 0;
        int IMFSampleGrabberSinkCallback.OnSetPresentationClock(IntPtr clock) => 0;
        int IMFSampleGrabberSinkCallback.OnShutdown() => 0;
    }

    /// <summary>
    /// Y'CbCr to RGB as 16.16 fixed-point factors: R = Y + RedV·Cr, G = Y −
    /// GreenU·Cb − GreenV·Cr, B = Y + BlueU·Cb, after scaling studio-range
    /// values (16–235, chroma 16–240) out to 0–255.
    /// </summary>
    internal sealed record YuvToRgb(int LumaOffset, int Luma, int RedV, int GreenU, int GreenV, int BlueU)
    {
        /// <param name="matrix">An MFVideoTransferMatrix; unknown means BT.709 for HD and BT.601 below it.</param>
        public static YuvToRgb For(uint matrix, bool hd, bool fullRange)
        {
            var (kr, kb) = matrix switch
            {
                1 => (0.2126, 0.0722),                // BT.709
                2 => (0.299, 0.114),                  // BT.601
                3 => (0.212, 0.087),                  // SMPTE 240M
                4 or 5 => (0.2627, 0.0593),           // BT.2020
                _ => hd ? (0.2126, 0.0722) : (0.299, 0.114),
            };
            var kg = 1 - kr - kb;
            var luma = fullRange ? 1.0 : 255.0 / 219;
            var chroma = fullRange ? 1.0 : 255.0 / 224;
            int Fixed(double value) => (int)Math.Round(value * 65536);
            return new YuvToRgb(
                fullRange ? 0 : 16,
                Fixed(luma),
                Fixed(chroma * (2 - 2 * kr)),
                Fixed(chroma * (2 - 2 * kb) * kb / kg),
                Fixed(chroma * (2 - 2 * kr) * kr / kg),
                Fixed(chroma * (2 - 2 * kb)));
        }
    }

    /// <summary>
    /// Calls through vtables, for objects used from Media Foundation's threads,
    /// where a runtime wrapper would try to marshal the call to the apartment
    /// that made it. Slots count IUnknown's three methods.
    /// </summary>
    private static unsafe class Vtable
    {
        private static void** Of(IntPtr obj) => *(void***)obj;

        /// <summary>IMFActivate::ActivateObject, after IMFAttributes' 30 methods.</summary>
        public static int ActivateObject(IntPtr activate, Guid iid, out IntPtr obj)
        {
            var result = IntPtr.Zero;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Of(activate)[33])(activate, &iid, &result);
            obj = result;
            return hr;
        }

        /// <summary>IMFStreamSink::GetMediaTypeHandler, then IMFMediaTypeHandler::GetCurrentMediaType.</summary>
        public static int GetCurrentMediaType(IntPtr streamSink, out IntPtr type)
        {
            IntPtr handler = IntPtr.Zero, result = IntPtr.Zero;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Of(streamSink)[9])(streamSink, &handler);
            if (hr >= 0)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Of(handler)[7])(handler, &result);
                Marshal.Release(handler);
            }
            type = result;
            return hr;
        }

        /// <summary>IMFAttributes::GetUINT32.</summary>
        public static int GetUInt32(IntPtr attributes, Guid key, out uint value)
        {
            uint result = 0;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Of(attributes)[7])(attributes, &key, &result);
            value = result;
            return hr;
        }

        /// <summary>IMFAttributes::GetUINT64.</summary>
        public static int GetUInt64(IntPtr attributes, Guid key, out ulong value)
        {
            ulong result = 0;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Of(attributes)[8])(attributes, &key, &result);
            value = result;
            return hr;
        }

        /// <summary>An MFVideoArea blob (IMFAttributes::GetBlob) as a rectangle; its offsets are 16.16 fixed point.</summary>
        public static Rect? GetArea(IntPtr attributes, Guid key)
        {
            var area = stackalloc int[4];
            uint size = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int*, uint, uint*, int>)Of(attributes)[15])(attributes, &key, area, 16, &size) < 0
                || size != 16 || area[2] <= 0 || area[3] <= 0)
                return null;
            int left = area[0] >> 16, top = area[1] >> 16;
            return new Rect { Left = left, Top = top, Right = left + area[2], Bottom = top + area[3] };
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
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MF_MT_GEOMETRIC_APERTURE = new("66758743-7e5f-400d-980a-aa8596c85696");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private const uint MFNominalRange_0_255 = 1;
    private static readonly Guid MR_STREAM_VOLUME_SERVICE = new("f8b5fa2f-32ef-46f5-b172-1321212fb2c4");

    [DllImport("mfplay.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFPCreateMediaPlayer(string? url, [MarshalAs(UnmanagedType.Bool)] bool startPlayback, int options,
        IMFPMediaPlayerCallback callback, IntPtr hwnd, out IMFPMediaPlayer player);

    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFCreateAudioRenderer(IntPtr attributes, out IMFMediaSink sink);

    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFCreateSampleGrabberSinkActivate(IntPtr mediaType, IMFSampleGrabberSinkCallback callback, out IntPtr activate);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr mediaType);

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
    // The sample grabber and the media type it asks for (mfidl.h, mfobjects.h)
    // ---------------------------------------------------------------------

    // IMFClockStateSink's methods, then its own.
    [ComImport, Guid("8c7b80bf-ee42-4b59-b1df-55668e1bdca8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSampleGrabberSinkCallback
    {
        [PreserveSig] int OnClockStart(long systemTime, long clockStartOffset);
        [PreserveSig] int OnClockStop(long systemTime);
        [PreserveSig] int OnClockPause(long systemTime);
        [PreserveSig] int OnClockRestart(long systemTime);
        [PreserveSig] int OnClockSetRate(long systemTime, float rate);
        [PreserveSig] int OnSetPresentationClock(IntPtr clock);
        [PreserveSig] int OnProcessSample(in Guid majorMediaType, uint sampleFlags, long sampleTime, long sampleDuration, IntPtr sampleBuffer, uint sampleSize);
        [PreserveSig] int OnShutdown();
    }

    // Every method, in vtable order, even the unused ones.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(in Guid key, IntPtr value);
        [PreserveSig] int GetItemType(in Guid key, out int type);
        [PreserveSig] int CompareItem(in Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IntPtr other, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32(in Guid key, out uint value);
        [PreserveSig] int GetUINT64(in Guid key, out ulong value);
        [PreserveSig] int GetDouble(in Guid key, out double value);
        [PreserveSig] int GetGUID(in Guid key, out Guid value);
        [PreserveSig] int GetStringLength(in Guid key, out uint length);
        [PreserveSig] int GetString(in Guid key, IntPtr value, uint size, IntPtr length);
        [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(in Guid key, out uint size);
        [PreserveSig] int GetBlob(in Guid key, IntPtr buffer, uint size, IntPtr blobSize);
        [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(in Guid key, in Guid iid, out IntPtr obj);
        [PreserveSig] int SetItem(in Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(in Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid key, uint value);
        [PreserveSig] int SetUINT64(in Guid key, ulong value);
        [PreserveSig] int SetDouble(in Guid key, double value);
        [PreserveSig] int SetGUID(in Guid key, in Guid value);
        [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(in Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown(in Guid key, IntPtr obj);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
    }

    // ---------------------------------------------------------------------
    // Win32 window for the video
    // ---------------------------------------------------------------------

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const uint WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_LBUTTONUP = 0x0202;
    private const int BLACK_BRUSH = 4;
    private const int HALFTONE = 4;
    private const uint DIB_RGB_COLORS = 0, SRCCOPY = 0x00CC0020;
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
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight, byte[] bits, ref BitmapInfoHeader info, uint usage, uint rop);

    /// <summary>BITMAPINFOHEADER; 32-bit RGB needs no color table after it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
