using System.Globalization;
using System.Reflection;

namespace Grepdesk.UI;

/// <summary>The running build's version and internal build number, from Directory.Build.props.</summary>
internal static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/FurkanKirat/Grepdesk";

    // e.g. "1.0.0+4e8f06f2c1…": the SDK appends the commit the build came from.
    private static readonly string Informational =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>"1.0.0"</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>Monotonically increasing internal build number (1, 2, 3...), or 0 if unavailable.</summary>
    public static int InternalBuild { get; } = ParseInternalBuild(typeof(AppInfo).Assembly);

    /// <summary>Short commit hash, or null when built outside a git checkout.</summary>
    public static string? Commit { get; } =
        Informational.Split('+') is [_, var hash, ..] && hash.Length > 0 ? hash[..Math.Min(7, hash.Length)] : null;

    /// <summary>"1.0.0 #1 (4e8f06f)", for display and logs.</summary>
    public static string Display => FormatDisplay(Version, InternalBuild, Commit);

    internal static int ParseInternalBuild(Assembly assembly)
    {
        var raw = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "InternalBuildNumber")?.Value;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build) && build > 0
            ? build
            : 0;
    }

    internal static string FormatDisplay(string version, int internalBuild, string? commit)
    {
        var withBuild = internalBuild > 0 ? $"{version} #{internalBuild}" : version;
        return string.IsNullOrEmpty(commit) ? withBuild : $"{withBuild} ({commit})";
    }
}
