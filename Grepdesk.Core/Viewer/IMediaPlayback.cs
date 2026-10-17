using System;

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

    void Play();

    void Pause();

    /// <summary>Redraws the current frame after the surface was resized or uncovered.</summary>
    void UpdateVideo();
}
