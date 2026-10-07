using System;
using Grepdesk.Core;
using Grepdesk.Core.Preview;
using Grepdesk.Core.Transfer;
using Grepdesk.Linux;
using Grepdesk.MacOS;
using Grepdesk.Windows;

namespace Grepdesk.UI.Helpers;

public static class PlatformShellFactory
{
    public static IPlatformShell CreatePlatformShell()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsShell();
        if (OperatingSystem.IsLinux())
            return new LinuxShell();
        if (OperatingSystem.IsMacOS())
            return new MacShell();

        throw new PlatformNotSupportedException("Unsupported operating system.");
    }

    /// <summary>File-manager context menu entries; null where not supported (macOS: Finder needs a signed extension).</summary>
    public static IShellIntegration? CreateShellIntegration()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsShellIntegration();
        if (OperatingSystem.IsLinux())
            return new LinuxShellIntegration();
        return null;
    }

    public static IDriveKindProvider CreateDriveKindProvider()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsDriveKindProvider();
        if (OperatingSystem.IsLinux())
            return new LinuxDriveKindProvider();
        if (OperatingSystem.IsMacOS())
            return new MacDriveKindProvider();
        return new BasicDriveKindProvider();
    }

    public static IFileCopier CreateFileCopier() =>
        OperatingSystem.IsWindows() ? new CopyFile2Copier() : new NativeFileCopier();

    /// <summary>Reads files copied/cut in the file manager; null where not supported.</summary>
    public static IFileClipboard? CreateFileClipboard()
    {
        if (OperatingSystem.IsWindows())
            return new Win32FileClipboard();
        if (OperatingSystem.IsLinux())
            return new LinuxFileClipboard();
        return null;
    }

    /// <summary>The file manager's thumbnails and media metadata; null where not supported.</summary>
    public static IShellPreview? CreateShellPreview() =>
        OperatingSystem.IsWindows() ? new WindowsShellPreview() : null;
}
