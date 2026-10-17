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

    /// <summary>The file can't be played (missing codec, corrupt file), or playback broke down.</summary>
    event Action<MediaError>? Failed;

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

public enum MediaProblem
{
    /// <summary>Windows has no decoder for the file's video or audio (HEVC and AV1 come from the Store).</summary>
    CodecMissing,
    /// <summary>Not a container Windows reads, or a damaged file.</summary>
    UnsupportedFile,
    /// <summary>Media Foundation isn't installed (Windows N and KN editions, without the Media Feature Pack).</summary>
    NoMediaFoundation,
    /// <summary>The native window video is drawn into couldn't be created.</summary>
    NoVideoWindow,
    Other
}

/// <summary>Why a file couldn't be played, with what the log needs to tell the cases apart.</summary>
/// <param name="HResult">The OS's error code, or the exception's.</param>
/// <param name="Stage">Where it failed ("open", "playback", "video window"...).</param>
public sealed record MediaError(int HResult, string Stage, Exception? Exception = null)
{
    public const string VideoWindowStage = "video window";

    public MediaProblem Problem => Stage == VideoWindowStage ? MediaProblem.NoVideoWindow : unchecked((uint)HResult) switch
    {
        0xC00D5212 => MediaProblem.CodecMissing,                   // MF_E_TOPO_CODEC_NOT_FOUND
        0xC00D36C4 or 0xC00D36B4 => MediaProblem.UnsupportedFile,  // MF_E_UNSUPPORTED_BYTESTREAM_TYPE, MF_E_INVALIDMEDIATYPE
        0x80131524 or 0x80131523 => MediaProblem.NoMediaFoundation, // DllNotFound, EntryPointNotFound
        _ => MediaProblem.Other,
    };

    public override string ToString() =>
        $"{Stage}: 0x{HResult:X8} ({Problem}){(Exception is null ? "" : Environment.NewLine + Exception)}";
}

/// <param name="Name">The track's title in the file, if it has one.</param>
/// <param name="Language">An RFC 1766 tag such as "en" or "tr-TR", if the file says.</param>
/// <param name="Channels">2 for stereo, 6 for 5.1; 0 if unknown.</param>
public sealed record AudioTrack(string? Name, string? Language, int Channels);
