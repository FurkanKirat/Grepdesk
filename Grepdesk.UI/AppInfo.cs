using System.Reflection;

namespace Grepdesk.UI;

/// <summary>The running build's version, from Directory.Build.props.</summary>
internal static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/FurkanKirat/Grepdesk";

    // e.g. "1.0.0+4e8f06f2c1…": the SDK appends the commit the build came from.
    private static readonly string Informational =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>"1.0.0"</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>Short commit hash, or null when built outside a git checkout.</summary>
    public static string? Commit { get; } =
        Informational.Split('+') is [_, var hash, ..] && hash.Length > 0 ? hash[..Math.Min(7, hash.Length)] : null;

    /// <summary>"1.0.0 (4e8f06f)", for display and logs.</summary>
    public static string Display => Commit is null ? Version : $"{Version} ({Commit})";
}
