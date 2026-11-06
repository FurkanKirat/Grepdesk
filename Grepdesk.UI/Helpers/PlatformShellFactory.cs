using System;
using Grepdesk.Core;
using Grepdesk.Core.Preview;
using Grepdesk.Core.Transfer;
using Grepdesk.Core.Viewer;
#if PLATFORM_WINDOWS
using Grepdesk.Windows;
#elif PLATFORM_LINUX
using Grepdesk.Linux;
#elif PLATFORM_MACOS
using Grepdesk.MacOS;
#endif

namespace Grepdesk.UI.Helpers;

#pragma warning disable CA1416 // Factory acts as the platform boundary

public static class PlatformShellFactory
{
    public static IPlatformShell CreatePlatformShell()
    {
#if PLATFORM_WINDOWS
        return new WindowsShell();
#elif PLATFORM_LINUX
        return new LinuxShell();
#elif PLATFORM_MACOS
        return new MacShell();
#else
        throw new PlatformNotSupportedException("Unsupported operating system.");
#endif
    }

    /// <summary>File-manager context menu entries; null where not supported (macOS: Finder needs a signed extension).</summary>
    public static IShellIntegration? CreateShellIntegration()
    {
#if PLATFORM_WINDOWS
        return new WindowsShellIntegration();
#elif PLATFORM_LINUX
        return new LinuxShellIntegration();
#else
        return null;
#endif
    }

    public static IDriveKindProvider CreateDriveKindProvider()
    {
#if PLATFORM_WINDOWS
        return new WindowsDriveKindProvider();
#elif PLATFORM_LINUX
        return new LinuxDriveKindProvider();
#elif PLATFORM_MACOS
        return new MacDriveKindProvider();
#else
        return new BasicDriveKindProvider();
#endif
    }

    public static IFileCopier CreateFileCopier() =>
#if PLATFORM_WINDOWS
        new CopyFile2Copier();
#else
        new NativeFileCopier();
#endif

    /// <summary>Reads files copied/cut in the file manager; null where not supported.</summary>
    public static IFileClipboard? CreateFileClipboard()
    {
#if PLATFORM_WINDOWS
        return new Win32FileClipboard();
#elif PLATFORM_LINUX
        return new LinuxFileClipboard();
#else
        return null;
#endif
    }

    /// <summary>The file manager's thumbnails and media metadata; null where not supported.</summary>
    public static IShellPreview? CreateShellPreview() =>
#if PLATFORM_WINDOWS
        new WindowsShellPreview();
#else
        null;
#endif

    /// <summary>The file manager's file and folder icons; null where not supported (a type badge is drawn instead).</summary>
    public static IShellIcons? CreateShellIcons() =>
#if PLATFORM_WINDOWS
        new WindowsShellIcons();
#else
        null;
#endif

    /// <summary>Video and audio playback in the viewer; null where not supported.</summary>
    public static IMediaPlayback? CreateMediaPlayback() =>
#if PLATFORM_WINDOWS
        new MediaFoundationPlayback();
#else
        null;
#endif
}
