using Grepdesk.Windows;

namespace Grepdesk.Tests;

public class InstallerTests
{
    /// <summary>
    /// The uninstaller deletes the Explorer menu keys the app writes. A new
    /// menu entry missing from setup.iss would be left pointing at a deleted exe.
    /// </summary>
    [WindowsFact]
    public void Uninstaller_removes_every_explorer_menu_key()
    {
        var script = File.ReadAllText(FindRepoFile("setup.iss"));

        foreach (var key in WindowsShellIntegration.AllKeyPaths)
            Assert.Contains($"Root: HKCU; Subkey: \"{key}\"; Flags: dontcreatekey uninsdeletekey", script);
    }

    /// <summary>
    /// File associations are spread over every extension, so the uninstaller
    /// asks the app to remove them; a default-app choice left behind makes
    /// double-clicking those files do nothing.
    /// </summary>
    [WindowsFact]
    public void Uninstaller_runs_the_app_to_remove_file_associations()
    {
        var script = File.ReadAllText(FindRepoFile("setup.iss"));

        Assert.Contains("[UninstallRun]", script);
        Assert.Contains("Filename: \"{app}\\Grepdesk.UI.exe\"; Parameters: \"--unregister\"", script);
    }

    private static string FindRepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }
}
