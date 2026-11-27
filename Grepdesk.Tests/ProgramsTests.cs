using Grepdesk.Core.Programs;

namespace Grepdesk.Tests;

public class ProgramsTests
{
    [Fact]
    public void Bookmarks_manage_favorites_recent_and_custom_programs()
    {
        using var dir = new TempDir();
        var appA = dir.WriteFile("AppA.exe", "x");
        var appB = dir.WriteFile("AppB.exe", "x");
        var appC = dir.WriteFile("AppC.exe", "x");

        var bookmarks = new ProgramBookmarks(maxRecent: 2);

        // Favorites
        Assert.False(bookmarks.IsFavorite(appA));
        Assert.True(bookmarks.ToggleFavorite(appA));
        Assert.True(bookmarks.IsFavorite(appA));
        Assert.False(bookmarks.AddFavorite(appA)); // duplicate ignored
        Assert.False(bookmarks.ToggleFavorite(appA));
        Assert.False(bookmarks.IsFavorite(appA));

        // Recent (capped at maxRecent = 2, most recent first)
        bookmarks.RecordLaunch(appA);
        bookmarks.RecordLaunch(appB);
        bookmarks.RecordLaunch(appC);
        Assert.Equal(2, bookmarks.Recent.Count);
        Assert.Equal(appC, bookmarks.Recent[0]);
        Assert.Equal(appB, bookmarks.Recent[1]);

        // Re-launching moves to top
        bookmarks.RecordLaunch(appB);
        Assert.Equal(appB, bookmarks.Recent[0]);
        Assert.Equal(appC, bookmarks.Recent[1]);

        // Custom programs
        Assert.True(bookmarks.AddCustom(appA));
        Assert.True(bookmarks.IsCustom(appA));
        Assert.True(bookmarks.RemoveCustom(appA));
        Assert.False(bookmarks.IsCustom(appA));
    }

    [Fact]
    public void Scan_discovers_programs_filters_noise_and_deduplicates()
    {
        using var dir = new TempDir();
        var startMenu = Path.Combine(dir.Path, "StartMenu");
        var desktop = Path.Combine(dir.Path, "Desktop");
        var customDir = Path.Combine(dir.Path, "Portable");
        Directory.CreateDirectory(startMenu);
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(customDir);

        // Valid programs in Start Menu
        var editorDir = Path.Combine(startMenu, "Editors");
        Directory.CreateDirectory(editorDir);
        File.WriteAllText(Path.Combine(editorDir, "CodeStudio.exe"), "bin");
        File.WriteAllText(Path.Combine(startMenu, "Browser.exe"), "bin");

        // Duplicate on Desktop (same name) -> deduplicated
        File.WriteAllText(Path.Combine(desktop, "Browser - Shortcut.exe"), "bin");

        // Noise / uninstallers -> filtered out
        File.WriteAllText(Path.Combine(editorDir, "Uninstall CodeStudio.exe"), "bin");
        File.WriteAllText(Path.Combine(editorDir, "unins000.exe"), "bin");
        File.WriteAllText(Path.Combine(editorDir, "Readme.txt"), "text");

        // macOS .app bundle
        var macApp = Path.Combine(startMenu, "PhotoLab.app");
        Directory.CreateDirectory(macApp);
        File.WriteAllText(Path.Combine(macApp, "inner.exe"), "bin"); // should not recurse inside .app

        // Linux .desktop file (valid + hidden NoDisplay)
        File.WriteAllText(Path.Combine(startMenu, "term.desktop"),
            "[Desktop Entry]\nType=Application\nName=FastTerminal\nExec=/usr/bin/fast-term --arg\nCategories=System;TerminalEmulator;\n");
        File.WriteAllText(Path.Combine(startMenu, "hidden.desktop"),
            "[Desktop Entry]\nType=Application\nName=HiddenHelper\nNoDisplay=true\nExec=/usr/bin/helper\n");

        // Custom portable program outside roots
        var portableExe = Path.Combine(customDir, "MyTool.exe");
        File.WriteAllText(portableExe, "bin");

        var roots = new[]
        {
            (startMenu, ProgramSource.StartMenu),
            (desktop, ProgramSource.Desktop),
        };

        var results = ProgramCatalog.Scan(roots, customPaths: [portableExe]);
        var names = results.Select(r => r.Name).ToList();

        Assert.Equal(["Browser", "CodeStudio", "FastTerminal", "MyTool", "PhotoLab"], names);

        var codeStudio = results.Single(r => r.Name == "CodeStudio");
        Assert.Equal("Editors", codeStudio.Category);

        var portable = results.Single(r => r.Name == "MyTool");
        Assert.True(portable.IsCustom);
    }

    [Fact]
    public void FilterAndSort_pins_favorites_first_and_ranks_search_matches()
    {
        var entries = new List<ProgramEntry>
        {
            new("Alpha Player", @"C:\Apps\Alpha.exe", @"C:\Apps\Alpha.exe", "Media", ProgramSource.StartMenu),
            new("Visual Studio Code", @"C:\Apps\Code.exe", @"C:\Apps\Code.exe", "Development", ProgramSource.StartMenu),
            new("CodeLite", @"C:\Apps\CodeLite.exe", @"C:\Apps\CodeLite.exe", "Development", ProgramSource.StartMenu),
            new("Zeta Tool", @"C:\Apps\Zeta.exe", @"C:\Apps\Zeta.exe", "Utilities", ProgramSource.StartMenu),
        };

        var bookmarks = new ProgramBookmarks();
        bookmarks.AddFavorite(@"C:\Apps\Zeta.exe");
        bookmarks.RecordLaunch(@"C:\Apps\CodeLite.exe");
        bookmarks.RecordLaunch(@"C:\Apps\Alpha.exe");

        // 1. All filter without query: Favorites first, then alphabetical
        var all = ProgramCatalog.FilterAndSort(entries, bookmarks, query: "", ProgramFilter.All);
        Assert.Equal(["Zeta Tool", "Alpha Player", "CodeLite", "Visual Studio Code"], all.Select(e => e.Name));

        // 2. Favorites filter: only Zeta Tool
        var favs = ProgramCatalog.FilterAndSort(entries, bookmarks, query: "", ProgramFilter.Favorites);
        Assert.Equal(["Zeta Tool"], favs.Select(e => e.Name));

        // 3. Recent filter: most recent launch first (Alpha, then CodeLite)
        var recent = ProgramCatalog.FilterAndSort(entries, bookmarks, query: "", ProgramFilter.Recent);
        Assert.Equal(["Alpha Player", "CodeLite"], recent.Select(e => e.Name));

        // 4. Search query "Code": prefix match "CodeLite" ranks ahead of word-prefix "Visual Studio Code"
        var searchCode = ProgramCatalog.FilterAndSort(entries, bookmarks, query: "Code", ProgramFilter.All);
        Assert.Equal(["CodeLite", "Visual Studio Code"], searchCode.Select(e => e.Name));

        // 5. Acronym search "vsc" matches "Visual Studio Code"
        var searchAcronym = ProgramCatalog.FilterAndSort(entries, bookmarks, query: "vsc", ProgramFilter.All);
        Assert.Equal(["Visual Studio Code"], searchAcronym.Select(e => e.Name));
    }

    [Fact]
    public void Scan_reports_monotonic_progress_ending_at_one()
    {
        using var dir = new TempDir();
        var root1 = Path.Combine(dir.Path, "R1");
        var root2 = Path.Combine(dir.Path, "R2");
        Directory.CreateDirectory(root1);
        Directory.CreateDirectory(root2);
        File.WriteAllText(Path.Combine(root1, "One.exe"), "bin");
        File.WriteAllText(Path.Combine(root2, "Two.exe"), "bin");

        var fractions = new List<double>();
        var results = ProgramCatalog.Scan(
            [(root1, ProgramSource.StartMenu), (root2, ProgramSource.Desktop)],
            onProgress: f => fractions.Add(f));

        Assert.Equal(2, results.Count);
        Assert.Equal([0.5, 1.0], fractions);
    }
}
