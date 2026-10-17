using System;
using System.Collections.Generic;

namespace Grepdesk.Core.Viewer;

/// <summary>
/// Plays video and audio with the OS's own codecs, drawing video into a native
/// child window that the UI hosts. Only implemented on Windows (Media Foundation).
/// </summary>
public interface IMediaPlayback
{
    /// <summary>Creates the window video is drawn into, as a child of <paramref name="parent"/>.</summary>
    IntPtr CreateSurface(IntPtr parent);

    void DestroySurface(IntPtr surface);

    /// <summary>Starts loading and playing the file. Events arrive on the calling (UI) thread.</summary>
    IMediaPlayer Open(string path, IntPtr surface);
}

public interface IMediaPlayer : IDisposable
{
    /// <summary>The file is loaded: duration and video size are known.</summary>
    event Action? Ready;

    /// <summary>The file can't be played (missing codec, corrupt file); the HRESULT.</summary>
    event Action<int>? Failed;

    event Action? StateChanged;

    event Action? Ended;

    /// <summary>The user clicked the video.</summary>
    event Action? SurfaceClicked;

    bool IsPlaying { get; }

    /// <summary>Zero until <see cref="Ready"/>.</summary>
    TimeSpan Duration { get; }

    TimeSpan Position { get; set; }

    /// <summary>0..1.</summary>
    float Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>Width × height of the video, or null for audio.</summary>
    (int Width, int Height)? VideoSize { get; }

    /// <summary>
    /// The file's audio tracks, once <see cref="Ready"/>, when there is more
    /// than one (a recording with game and microphone on separate tracks, a
    /// film with several languages). All of them play together; each can be
    /// switched off. Empty for a single track.
    /// </summary>
    IReadOnlyList<AudioTrack> AudioTracks { get; }

    bool IsTrackEnabled(int track);

    /// <summary>Silences or restores one of <see cref="AudioTracks"/>, without interrupting playback.</summary>
    void SetTrackEnabled(int track, bool enabled);

    void Play();

    void Pause();

    /// <summary>Redraws the current frame after the surface was resized or uncovered.</summary>
    void UpdateVideo();
}

/// <param name="Name">The track's title in the file, if it has one.</param>
/// <param name="Language">An RFC 1766 tag such as "en" or "tr-TR", if the file says.</param>
/// <param name="Channels">2 for stereo, 6 for 5.1; 0 if unknown.</param>
public sealed record AudioTrack(string? Name, string? Language, int Channels);
