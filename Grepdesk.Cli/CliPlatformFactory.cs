using Grepdesk.Core;
using Grepdesk.Core.SystemInfo;
#if PLATFORM_WINDOWS
using Grepdesk.Windows;
#elif PLATFORM_LINUX
using Grepdesk.Linux;
#elif PLATFORM_MACOS
using Grepdesk.MacOS;
#endif

namespace Grepdesk.Cli;

#pragma warning disable CA1416 // Factory acts as the platform boundary

internal static class CliPlatformFactory
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

    public static ISystemInfoProvider CreateSystemInfoProvider() =>
#if PLATFORM_WINDOWS
        new WindowsSystemInfoProvider();
#elif PLATFORM_LINUX
        new LinuxSystemInfoProvider();
#elif PLATFORM_MACOS
        new MacSystemInfoProvider();
#else
        new BasicSystemInfoProvider();
#endif
}
