using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Grepdesk.Core;
using Grepdesk.Core.Viewer;
using Microsoft.Win32;

namespace Grepdesk.Windows;

/// <summary>
/// Explorer context-menu entries as classic registry verbs under HKCU
/// (no admin rights, current user only). On Windows 11 they appear under
/// "Show more options". "Open with" also registers Grepdesk as an app that
/// opens files, so it shows in Explorer's "Open with" list and can be picked
/// as the default app (Windows only lets the user make that choice).
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsShellIntegration : IShellIntegration
{
    private const string Classes = @"Software\Classes\";

    // %V = the folder whose background was clicked; %1 = the clicked item.
    private sealed record Verb(string ParentKey, string Name, string LabelKey, string Arguments, bool MultiSelect = false);

    private static readonly Dictionary<ShellFeature, Verb[]> Verbs = new()
    {
        [ShellFeature.OpenWith] =
        [
            // Key name kept as "Grepdesk" so entries written by earlier builds are recognised.
            new(@"Directory\Background\shell", "Grepdesk", "MenuOpenWith", "\"%V\""),
            new(@"Directory\shell", "Grepdesk", "MenuOpenWith", "\"%1\""),
            new(@"*\shell", "Grepdesk", "MenuOpenWith", "\"%1\"")
        ],
        [ShellFeature.Extract] =
        [
            // SystemFileAssociations shows the verb whatever program owns .zip.
            new(@"SystemFileAssociations\.zip\shell", "GrepdeskExtractHere", "MenuExtractHere", "--extract-here \"%1\"", MultiSelect: true),
            new(@"SystemFileAssociations\.zip\shell", "GrepdeskExtractTo", "MenuExtractTo", "--extract-to \"%1\"", MultiSelect: true)
        ],
        [ShellFeature.Compress] =
        [
            new(@"*\shell", "GrepdeskCompress", "MenuCompress", "--compress \"%1\"", MultiSelect: true),
            new(@"Directory\shell", "GrepdeskCompress", "MenuCompress", "--compress \"%1\"", MultiSelect: true)
        ],
        [ShellFeature.Paste] =
        [
            new(@"Directory\Background\shell", "GrepdeskPaste", "MenuPaste", "--paste \"%V\""),
            new(@"Directory\shell", "GrepdeskPaste", "MenuPaste", "--paste \"%1\"")
        ]
    };

    /// <summary>The file type Grepdesk offers for every extension it opens.</summary>
    private const string ProgId = "Grepdesk.File";
    private const string ProgIdKey = Classes + ProgId;

    /// <summary>
    /// Where Windows looks up an app picked in "Open with" by its exe name;
    /// it creates this key itself when the user browses to the exe.
    /// </summary>
    private const string ApplicationKey = Classes + @"Applications\Grepdesk.UI.exe";

    private static string KeyPath(Verb verb) => $@"{Classes}{verb.ParentKey}\{verb.Name}";

    /// <summary>
    /// Every key Grepdesk may write under HKCU. The installer deletes these on
    /// uninstall (setup.iss) as a fallback; a test keeps the two lists in sync.
    /// The values spread under each extension are removed by
    /// <c>Grepdesk.UI.exe --unregister</c>, which the uninstaller runs first.
    /// </summary>
    public static IEnumerable<string> AllKeyPaths =>
        Verbs.Values.SelectMany(v => v).Select(KeyPath).Append(ProgIdKey).Append(ApplicationKey).Distinct();

    private IReadOnlySet<string> _fileTypes =
        new HashSet<string>(ViewerFormats.OpenWithExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Only types the viewer can show; anything else (e.g. a launched type) is ignored.</summary>
    public void SetOpenWithFileTypes(IEnumerable<string> extensions)
    {
        var types = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        types.IntersectWith(ViewerFormats.AssociableExtensions);
        _fileTypes = types;
    }

    public bool IsEnabled(ShellFeature feature)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath(Verbs[feature][0]));
        return key is not null;
    }

    public ShellActionResult Enable(ShellFeature feature, string executablePath, Func<string, string> label)
    {
        try
        {
            foreach (var verb in Verbs[feature])
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath(verb));
                key.SetValue("", label(verb.LabelKey));
                key.SetValue("Icon", $"\"{executablePath}\"");

                // Without this, Explorer hides the verb when more than 15 items
                // are selected. Each item still starts its own process; the
                // single-instance host merges them into one job.
                if (verb.MultiSelect)
                    key.SetValue("MultiSelectModel", "Player");

                using var command = key.CreateSubKey("command");
                command.SetValue("", $"\"{executablePath}\" {verb.Arguments}");
            }
            if (feature == ShellFeature.OpenWith)
                RegisterFileTypes(executablePath, label("MenuOpenWith"));
            return ShellActionResult.Success(null);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
        }
    }

    public ShellActionResult Disable(ShellFeature feature)
    {
        try
        {
            foreach (var verb in Verbs[feature])
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath(verb), throwOnMissingSubKey: false);
            if (feature == ShellFeature.OpenWith)
                UnregisterFileTypes();
            return ShellActionResult.Success(null);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
        }
    }

    private void RegisterFileTypes(string executablePath, string openLabel)
    {
        bool firstTime;
        using (var existing = Registry.CurrentUser.OpenSubKey(ProgIdKey)) firstTime = existing is null;
        var command = $"\"{executablePath}\" \"%1\"";
        var icon = $"\"{executablePath}\",0";

        using (var progId = Registry.CurrentUser.CreateSubKey(ProgIdKey))
        {
            progId.SetValue("", "Grepdesk");
            using (var defaultIcon = progId.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using var open = progId.CreateSubKey(@"shell\open");
            open.SetValue("", openLabel);
            using var openCommand = open.CreateSubKey("command");
            openCommand.SetValue("", command);
        }

        using (var app = Registry.CurrentUser.CreateSubKey(ApplicationKey))
        {
            app.SetValue("FriendlyAppName", "Grepdesk");
            using (var defaultIcon = app.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using (var openCommand = app.CreateSubKey(@"shell\open\command")) openCommand.SetValue("", command);
            app.DeleteSubKeyTree("SupportedTypes", throwOnMissingSubKey: false);
            using var supported = app.CreateSubKey("SupportedTypes");
            foreach (var ext in _fileTypes) supported.SetValue(ext, "");
        }

        var registered = _fileTypes;
        var added = false;
        foreach (var ext in registered)
        {
            using var progIds = Registry.CurrentUser.CreateSubKey($@"{Classes}{ext}\OpenWithProgids");
            added |= !progIds.GetValueNames().Contains(ProgId);
            progIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Types the user unticked in settings, and ones earlier builds registered
        // (like .bat and .cmd): take Grepdesk off every extension not in the list.
        var dropped = RemoveAssociations(ext => !registered.Contains(ext));

        // Rewritten on every start to follow a moved exe; only a changed
        // registration needs Explorer to reload its associations.
        if (firstTime || added || dropped) NotifyAssociationsChanged();
    }

    private static void UnregisterFileTypes()
    {
        RemoveAssociations(_ => true);
        Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ApplicationKey, throwOnMissingSubKey: false);
        NotifyAssociationsChanged();
    }

    private const string FileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    private const string Toasts = @"Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts";

    /// <summary>The names Windows knows Grepdesk by in an extension's association keys.</summary>
    private static readonly string[] OurProgIds = [ProgId, @"Applications\Grepdesk.UI.exe"];
    private const string ExeName = "Grepdesk.UI.exe";

    /// <summary>
    /// Takes Grepdesk off the extensions <paramref name="matches"/> picks, wherever
    /// Windows recorded it: the offer under Classes, and Explorer's own records of
    /// the user's choice. A default-app choice ("Always use this app") left pointing
    /// at a deleted Grepdesk makes double-clicking those files do nothing, so it is
    /// removed and Windows falls back to the type's normal handler. Choices for
    /// other apps are left alone. Returns whether anything was removed.
    /// </summary>
    private static bool RemoveAssociations(Func<string, bool> matches)
    {
        var removed = false;
        var hkcu = Registry.CurrentUser;

        using (var classes = hkcu.OpenSubKey(Classes, writable: true))
        {
            foreach (var ext in Extensions(classes, matches))
            {
                if (!DeleteValues(classes!, $@"{ext}\OpenWithProgids", n => n == ProgId)) continue;
                removed = true;
                DeleteIfEmpty(classes!, $@"{ext}\OpenWithProgids");
                DeleteIfEmpty(classes!, ext);
            }
        }

        using (var fileExts = hkcu.OpenSubKey(FileExts, writable: true))
        {
            foreach (var ext in Extensions(fileExts, matches))
            {
                foreach (var choice in (string[])["UserChoice", "UserChoiceLatest"])
                {
                    if (!IsOurs(ChoiceProgId(fileExts!, $@"{ext}\{choice}"))) continue;
                    TryDelete(fileExts!, $@"{ext}\{choice}");
                    removed = true;
                }
                removed |= DeleteValues(fileExts!, $@"{ext}\OpenWithProgids", IsOurs);
                removed |= RemoveFromOpenWithList(fileExts!, $@"{ext}\OpenWithList");
            }
        }

        // "New app available" prompts Windows has shown or queued for Grepdesk,
        // named "<progid>_<ext>".
        using (var toasts = hkcu.OpenSubKey(Toasts, writable: true))
        {
            foreach (var name in toasts?.GetValueNames() ?? [])
            {
                var split = name.LastIndexOf('_');
                if (split > 0 && IsOurs(name[..split]) && matches(name[(split + 1)..]))
                    toasts!.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        return removed;
    }

    private static bool IsOurs(string? progId) =>
        progId is not null && OurProgIds.Contains(progId, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Extensions(RegistryKey? parent, Func<string, bool> matches) =>
        parent?.GetSubKeyNames().Where(n => n.Length > 1 && n[0] == '.' && matches(n)) ?? [];

    /// <summary>UserChoice keeps the ProgId as a value; Windows 11's UserChoiceLatest keeps it in a ProgId subkey.</summary>
    private static string? ChoiceProgId(RegistryKey parent, string path)
    {
        using var choice = parent.OpenSubKey(path);
        if (choice is null) return null;
        if (choice.GetValue("ProgId") is string direct) return direct;
        using var nested = choice.OpenSubKey("ProgId");
        return nested?.GetValue("ProgId") as string;
    }

    private static bool DeleteValues(RegistryKey parent, string path, Func<string, bool> matches)
    {
        using var key = OpenWritable(parent, path);
        if (key is null) return false;
        var names = key.GetValueNames().Where(matches).ToList();
        foreach (var name in names) key.DeleteValue(name, throwOnMissingValue: false);
        return names.Count > 0;
    }

    /// <summary>OpenWithList holds apps as letter values, ordered by an MRUList string of those letters.</summary>
    private static bool RemoveFromOpenWithList(RegistryKey parent, string path)
    {
        using var key = OpenWritable(parent, path);
        if (key is null) return false;
        var ours = key.GetValueNames()
            .Where(n => n.Length == 1 && string.Equals(key.GetValue(n) as string, ExeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (ours.Count == 0) return false;

        foreach (var name in ours) key.DeleteValue(name, throwOnMissingValue: false);
        if (key.GetValue("MRUList") is string mru)
            key.SetValue("MRUList", new string(mru.Where(c => !ours.Contains(c.ToString())).ToArray()));
        return true;
    }

    /// <summary>
    /// Opens a key for writing; some association keys deny the user write access,
    /// and one such key must not stop the rest of the cleanup.
    /// </summary>
    private static RegistryKey? OpenWritable(RegistryKey parent, string path)
    {
        try { return parent.OpenSubKey(path, writable: true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static void TryDelete(RegistryKey parent, string path)
    {
        try { parent.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
    }

    /// <summary>Removes a key Grepdesk's entry was the only thing in, so no empty extension keys are left behind.</summary>
    private static void DeleteIfEmpty(RegistryKey parent, string path)
    {
        using (var key = parent.OpenSubKey(path))
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
        TryDelete(parent, path);
    }

    private static void NotifyAssociationsChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
