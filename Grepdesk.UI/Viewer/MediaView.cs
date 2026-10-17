using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// Video and audio with play/pause, a seek bar and volume. Video is drawn by
/// the OS into a native child window, so nothing of ours may overlap it: the
/// controls sit in a bar below. Space or K plays and pauses, the arrow keys
/// seek 5 s, M mutes, 1-9 switch audio tracks on and off when there are several.
/// </summary>
internal sealed class MediaView : DockPanel, IViewerBody
{
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);
    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly IMediaPlayback _playback;
    private readonly string _path;
    private readonly Bitmap? _art;
    private readonly Button _playButton;
    private readonly Button _muteButton;
    private readonly Button _tracksButton;
    private readonly Slider _seek;
    private readonly Slider _volume;
    private readonly TextBlock _timeText;
    private readonly DispatcherTimer _timer;
    private IMediaPlayer? _player;
    private TimeSpan? _pendingSeek;
    private bool _updatingSeek;
    private bool _failed;

    /// <summary>The OS couldn't play the file; the HRESULT.</summary>
    public event Action<int>? Failed;

    /// <summary>Video size and duration, once known, for the window's info line.</summary>
    public event Action<string>? InfoChanged;

    /// <param name="art">Album art or a frame from the file manager, shown for audio.</param>
    public MediaView(IMediaPlayback playback, string path, bool video, Bitmap? art)
    {
        _playback = playback;
        _path = path;
        _art = art;
        Focusable = true;

        _playButton = ViewerBar.Button("▶", Loc.Get("ViewerPlayPause"));
        _playButton.Click += (_, _) => TogglePlay();
        _muteButton = ViewerBar.Button("🔊", Loc.Get("ViewerMute"));
        _muteButton.Click += (_, _) => ToggleMute();
        _tracksButton = ViewerBar.Button("🎧", Loc.Get("ViewerAudioTracks"));
        _tracksButton.IsVisible = false;
        _tracksButton.Click += (_, _) => ShowTracks();
        _timeText = ViewerBar.Label("0:00 / 0:00", 96);

        _seek = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        _seek.ValueChanged += (_, e) =>
        {
            if (!_updatingSeek) _pendingSeek = TimeSpan.FromSeconds(e.NewValue);
        };
        _volume = new Slider { Minimum = 0, Maximum = 1, Value = 1, Width = 90, VerticalAlignment = VerticalAlignment.Center };
        _volume.ValueChanged += (_, e) => { if (_player is not null) _player.Volume = (float)e.NewValue; };

        var left = ViewerBar.Row(_playButton, _timeText);
        var right = ViewerBar.Row(_tracksButton, _muteButton, _volume);
        var bar = new DockPanel { Children = { left, right, _seek } };
        SetDock(left, Dock.Left);
        SetDock(right, Dock.Right);
        Children.Add(ViewerBar.Create(bar));

        if (video)
        {
            var host = new VideoHost(this);
            Children.Add(host);
        }
        else
        {
            Children.Add(AudioCard(path, art));
            Dispatcher.UIThread.Post(() => Start(IntPtr.Zero), DispatcherPriority.Loaded);
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) => Tick());
        _timer.Start();
    }

    private static Control AudioCard(string path, Bitmap? art)
    {
        var card = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(art is not null
            ? new Image { Source = art, MaxWidth = 320, MaxHeight = 320 }
            : new TextBlock { Text = "♪", FontSize = 96, Foreground = ViewerBar.Muted, HorizontalAlignment = HorizontalAlignment.Center });
        card.Children.Add(new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(path),
            Foreground = ViewerBar.Text,
            FontSize = 16,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
        });
        return card;
    }

    /// <summary>Starts playback into the surface (or no surface, for audio).</summary>
    private void Start(IntPtr surface)
    {
        if (_player is not null) return;
        try
        {
            _player = _playback.Open(_path, surface);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            RaiseFailed(e.HResult);
            return;
        }
        _player.Ready += OnReady;
        _player.Failed += RaiseFailed;
        _player.StateChanged += () => _playButton.Content = _player?.IsPlaying == true ? "❚❚" : "▶";
        _player.SurfaceClicked += TogglePlay;
        _player.Volume = (float)_volume.Value;
    }

    /// <summary>
    /// Once, and after the player's callback has returned: the window reacts
    /// by disposing the player, which must not happen inside its own callback.
    /// </summary>
    private void RaiseFailed(int hr)
    {
        if (_failed) return;
        _failed = true;
        Dispatcher.UIThread.Post(() => Failed?.Invoke(hr));
    }

    public void FocusContent() => Focus();

    private void OnReady()
    {
        if (_player is null) return;
        _updatingSeek = true;
        _seek.Maximum = Math.Max(0.1, _player.Duration.TotalSeconds);
        _updatingSeek = false;

        var parts = new List<string>();
        if (_player.VideoSize is var (w, h)) parts.Add($"{w} × {h}");
        if (_player.Duration > TimeSpan.Zero) parts.Add(Time(_player.Duration));
        InfoChanged?.Invoke(string.Join("  ·  ", parts));
        UpdateTracksButton();
    }

    private void UpdateTracksButton()
    {
        var tracks = _player?.AudioTracks ?? [];
        _tracksButton.IsVisible = tracks.Count > 1;
        var on = Enumerable.Range(0, tracks.Count).Count(i => _player!.IsTrackEnabled(i));
        _tracksButton.Content = $"🎧 {on}/{tracks.Count}";
    }

    private void ShowTracks()
    {
        if (_player is not { AudioTracks.Count: > 1 } player) return;
        var items = new List<MenuItem>();
        for (var i = 0; i < player.AudioTracks.Count; i++)
        {
            var track = i;
            var item = new MenuItem
            {
                Header = TrackLabel(track, player.AudioTracks[track]),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = player.IsTrackEnabled(track),
                StaysOpenOnClick = true,
                InputGesture = track < 9 ? new KeyGesture(Key.D1 + track) : null,
            };
            item.Click += (_, _) =>
            {
                ToggleTrack(track);
                item.IsChecked = player.IsTrackEnabled(track);
            };
            items.Add(item);
        }
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.TopEdgeAlignedRight }.Open(_tracksButton);
    }

    /// <summary>"Track 2 · Microphone · en · 5.1", leaving out what the file doesn't say.</summary>
    private static string TrackLabel(int index, AudioTrack track)
    {
        var parts = new List<string> { Loc.Get("ViewerAudioTrack", index + 1) };
        if (track.Name is { } name) parts.Add(name);
        if (track.Language is { } language) parts.Add(language);
        parts.Add(track.Channels switch
        {
            1 => Loc.Get("ViewerAudioMono"),
            2 => Loc.Get("ViewerAudioStereo"),
            6 => "5.1",
            8 => "7.1",
            > 0 => Loc.Get("ViewerAudioChannels", track.Channels),
            _ => "",
        });
        return string.Join("  ·  ", parts.Where(p => p.Length > 0));
    }

    private void ToggleTrack(int track)
    {
        if (_player is null || track >= _player.AudioTracks.Count) return;
        _player.SetTrackEnabled(track, !_player.IsTrackEnabled(track));
        UpdateTracksButton();
    }

    private void Tick()
    {
        if (_player is null) return;
        if (_pendingSeek is { } seek)
        {
            _pendingSeek = null;
            _player.Position = seek;
        }
        else if (!_seek.IsPointerOver || !_player.IsPlaying)
        {
            _updatingSeek = true;
            _seek.Value = Math.Min(_seek.Maximum, _player.Position.TotalSeconds);
            _updatingSeek = false;
        }
        _timeText.Text = $"{Time(TimeSpan.FromSeconds(_seek.Value))} / {Time(_player.Duration)}";
    }

    private static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void TogglePlay()
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    private void ToggleMute()
    {
        if (_player is null) return;
        _player.IsMuted = !_player.IsMuted;
        _muteButton.Content = _player.IsMuted ? "🔇" : "🔊";
    }

    private void SeekBy(TimeSpan delta)
    {
        if (_player is null) return;
        _pendingSeek = (_pendingSeek ?? _player.Position) + delta;
        if (_pendingSeek < TimeSpan.Zero) _pendingSeek = TimeSpan.Zero;
    }

    /// <summary>Space plays/pauses; in the quick viewer it closes the window instead.</summary>
    public bool SpaceToggles { get; init; } = true;

    public bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space when SpaceToggles:
            case Key.K:
                TogglePlay();
                return true;
            case Key.Left: SeekBy(-SeekStep); return true;
            case Key.Right: SeekBy(SeekStep); return true;
            case Key.M: ToggleMute(); return true;
            case >= Key.D1 and <= Key.D9 when _player is { AudioTracks.Count: > 1 } && e.KeyModifiers == KeyModifiers.None:
                ToggleTrack(e.Key - Key.D1);
                return true;
            default: return false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _player?.Dispose();
        _player = null;
        _art?.Dispose();
    }

    /// <summary>Hosts the native window the video is drawn into.</summary>
    private sealed class VideoHost : NativeControlHost
    {
        private readonly MediaView _owner;
        private IntPtr _surface;

        public VideoHost(MediaView owner) => _owner = owner;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _surface = _owner._playback.CreateSurface(parent.Handle);
            Dispatcher.UIThread.Post(() => _owner.Start(_surface), DispatcherPriority.Loaded);
            return new PlatformHandle(_surface, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            // The player draws into the window: it goes first.
            _owner._player?.Dispose();
            _owner._player = null;
            _owner._playback.DestroySurface(control.Handle);
            _surface = IntPtr.Zero;
        }
    }
}
