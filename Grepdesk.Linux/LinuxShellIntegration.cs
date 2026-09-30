using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grepdesk.Core;

namespace Grepdesk.Linux;

/// <summary>
/// Right-click menu entries for the common Linux file managers, all written
/// under the user's data folder (~/.local/share), no root needed:
/// <list type="bullet">
/// <item>KDE Dolphin — service menus in kio/servicemenus (*.desktop)</item>
/// <item>GNOME Files (Nautilus) — scripts in nautilus/scripts, shown under "Scripts"</item>
/// <item>Cinnamon Nemo — actions in nemo/actions (*.nemo_action)</item>
/// </list>
/// Entries for managers that aren't installed are harmless files.
/// Dolphin and Nemo pass every selected file to one process; Nautilus scripts
/// get them as arguments too, so no merging is needed on Linux.
/// </summary>
public class LinuxShellIntegration : IShellIntegration
{
    // Marks the Nautilus scripts we own: their file name is the (localised)
    // menu label, so after a language change the old name must be found and removed.
    private const string ScriptMarker = "# grepdesk-feature:";

    private readonly string _dataHome;

    public LinuxShellIntegration() : this(DefaultDataHome()) { }

    /// <summary>For tests: write under a scratch folder instead of ~/.local/share.</summary>
    public LinuxShellIntegration(string dataHome) => _dataHome = dataHome;

    private string DolphinDir => Path.Combine(_dataHome, "kio", "servicemenus");
    private string NautilusDir => Path.Combine(_dataHome, "nautilus", "scripts");
    private string NemoDir => Path.Combine(_dataHome, "nemo", "actions");

    private sealed record Verb(string Id, string LabelKey, string Flag, string MimeTypes, NemoTarget Nemo);

    /// <summary>What a Nemo action applies to: selected files (with an extension filter) or the empty background.</summary>
    private sealed record NemoTarget(bool Background, string Selection, string Extensions);

    private static readonly Dictionary<ShellFeature, Verb[]> Verbs = new()
    {
        [ShellFeature.OpenWith] =
        [
            new("open", "MenuOpenWith", "", "inode/directory;", new NemoTarget(true, "s", "dir;"))
        ],
        [ShellFeature.Extract] =
        [
            new("extract-here", "MenuExtractHere", "--extract-here", "application/zip;", new NemoTarget(false, "notnone", "zip;")),
            new("extract-to", "MenuExtractTo", "--extract-to", "application/zip;", new NemoTarget(false, "notnone", "zip;"))
        ],
        [ShellFeature.Compress] =
        [
            new("compress", "MenuCompress", "--compress", "all/all;", new NemoTarget(false, "notnone", "any;"))
        ],
        [ShellFeature.Paste] =
        [
            new("paste", "MenuPaste", "--paste", "inode/directory;", new NemoTarget(true, "s", "dir;"))
        ]
    };

    public bool IsEnabled(ShellFeature feature) =>
        File.Exists(DolphinFile(Verbs[feature][0]));

    public ShellActionResult Enable(ShellFeature feature, string executablePath, Func<string, string> label)
    {
        try
        {
            Disable(feature); // drops Nautilus scripts named in a previous language
            foreach (var verb in Verbs[feature])
            {
                var text = label(verb.LabelKey);
                WriteDolphin(verb, text, executablePath);
                WriteNautilus(feature, verb, text, executablePath);
                WriteNemo(verb, text, executablePath);
            }
            return ShellActionResult.Success(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
        }
    }

    public ShellActionResult Disable(ShellFeature feature)
    {
        try
        {
            foreach (var verb in Verbs[feature])
            {
                File.Delete(DolphinFile(verb));
                File.Delete(NemoFile(verb, background: false));
                File.Delete(NemoFile(verb, background: true));
            }

            if (Directory.Exists(NautilusDir))
            {
                var marker = $"{ScriptMarker}{feature}";
                foreach (var script in Directory.EnumerateFiles(NautilusDir))
                    if (File.ReadLines(script).Take(3).Any(l => l.StartsWith(marker, StringComparison.Ordinal)))
                        File.Delete(script);
            }
            return ShellActionResult.Success(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
        }
    }

    // ------------------------------------------------------------ Dolphin

    private string DolphinFile(Verb verb) => Path.Combine(DolphinDir, $"grepdesk-{verb.Id}.desktop");

    private void WriteDolphin(Verb verb, string label, string exe)
    {
        // %F: every selected item in one process. On the view's empty area
        // Dolphin applies directory menus to the folder being shown.
        var content = $"""
            [Desktop Entry]
            Type=Service
            MimeType={verb.MimeTypes}
            Actions=grepdesk
            X-KDE-ServiceTypes=KonqPopupMenu/Plugin
            X-KDE-Priority=TopLevel

            [Desktop Action grepdesk]
            Name={label}
            Icon=system-search
            Exec={ExecLine(exe, verb.Flag, "%F")}

            """;
        WriteExecutable(DolphinFile(verb), content);
    }

    private static string ExecLine(string exe, string flag, string token) =>
        flag.Length > 0 ? $"{DesktopQuote(exe)} {flag} {token}" : $"{DesktopQuote(exe)} {token}";

    // .desktop Exec quoting: wrap in double quotes, escape " ` $ and \ inside.
    private static string DesktopQuote(string path) =>
        "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";

    // ------------------------------------------------------------ Nautilus

    private void WriteNautilus(ShellFeature feature, Verb verb, string label, string exe)
    {
        // Selected files arrive as arguments; with nothing selected the script
        // runs in the folder being viewed, which is where "open"/"paste" should act.
        var arguments = feature is ShellFeature.OpenWith or ShellFeature.Paste ? "\"${1:-$PWD}\"" : "\"$@\"";
        var flag = verb.Flag.Length > 0 ? verb.Flag + " " : "";
        var content = $"""
            #!/bin/sh
            {ScriptMarker}{feature}
            exec {ShellQuote(exe)} {flag}{arguments}

            """;
        WriteExecutable(Path.Combine(NautilusDir, SafeFileName(label)), content);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string SafeFileName(string label) =>
        string.Concat(label.Select(c => c is '/' or '\0' ? '-' : c));

    // ------------------------------------------------------------ Nemo

    private string NemoFile(Verb verb, bool background) =>
        Path.Combine(NemoDir, background ? $"grepdesk-{verb.Id}-here.nemo_action" : $"grepdesk-{verb.Id}.nemo_action");

    private void WriteNemo(Verb verb, string label, string exe)
    {
        // %F: selected paths, %P: the folder being viewed (for the empty background).
        WriteExecutable(NemoFile(verb, background: false), NemoAction(label, exe, verb.Flag, "%F", verb.Nemo.Selection, verb.Nemo.Extensions));
        if (verb.Nemo.Background)
            WriteExecutable(NemoFile(verb, background: true), NemoAction(label, exe, verb.Flag, "%P", "none", "any;"));
    }

    private static string NemoAction(string label, string exe, string flag, string token, string selection, string extensions) => $"""
        [Nemo Action]
        Name={label}
        Comment={label}
        Exec={ExecLine(exe, flag, token)}
        Icon-Name=system-search
        Selection={selection}
        Extensions={extensions}

        """;

    // ------------------------------------------------------------ files

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        // Plasma 6 ignores service menus that aren't executable; scripts obviously must be.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static string DefaultDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }
}
