namespace Grepdesk.Core;

/// <summary>An entry Grepdesk can add to the OS file manager's right-click menu.</summary>
public enum ShellFeature
{
    /// <summary>"Open with Grepdesk" on folders and folder backgrounds.</summary>
    OpenWith,
    /// <summary>"Extract here" / "Extract to folder" on .zip files.</summary>
    Extract,
    /// <summary>"Compress with Grepdesk" on files and folders.</summary>
    Compress,
    /// <summary>"Paste with Grepdesk" on folders and folder backgrounds.</summary>
    Paste
}

/// <summary>
/// Adds and removes Grepdesk's entries in the file manager's context menu.
/// Only implemented where the platform supports it (Windows Explorer).
/// </summary>
public interface IShellIntegration
{
    bool IsEnabled(ShellFeature feature);

    /// <summary>
    /// Writes (or rewrites) the menu entries for a feature. Rewriting is cheap,
    /// so callers re-enable on startup to follow a moved exe or a language change.
    /// </summary>
    /// <param name="label">Maps a menu label key (e.g. "MenuExtractHere") to display text.</param>
    ShellActionResult Enable(ShellFeature feature, string executablePath, Func<string, string> label);

    ShellActionResult Disable(ShellFeature feature);
}
