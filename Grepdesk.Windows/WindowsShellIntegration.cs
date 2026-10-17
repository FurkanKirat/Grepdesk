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
    /// uninstall (setup.iss); a test keeps the two lists in sync. The
    /// "OpenWithProgids" values left under each extension point at the
    /// deleted <see cref="ProgId"/>, and Windows ignores them.
    /// </summary>
    public static IEnumerable<string> AllKeyPaths =>
        Verbs.Values.SelectMany(v => v).Select(KeyPath).Append(ProgIdKey).Append(ApplicationKey).Distinct();

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

    private static void RegisterFileTypes(string executablePath, string openLabel)
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
            using var supported = app.CreateSubKey("SupportedTypes");
            foreach (var ext in ViewerFormats.OpenWithExtensions) supported.SetValue(ext, "");
        }

        foreach (var ext in ViewerFormats.OpenWithExtensions)
        {
            using var progIds = Registry.CurrentUser.CreateSubKey($@"{Classes}{ext}\OpenWithProgids");
            progIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Rewritten on every start to follow a moved exe; only a new
        // registration needs Explorer to reload its associations.
        if (firstTime) NotifyAssociationsChanged();
    }

    private static void UnregisterFileTypes()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ApplicationKey, throwOnMissingSubKey: false);
        foreach (var ext in ViewerFormats.OpenWithExtensions)
        {
            using var progIds = Registry.CurrentUser.OpenSubKey($@"{Classes}{ext}\OpenWithProgids", writable: true);
            progIds?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        NotifyAssociationsChanged();
    }

    private static void NotifyAssociationsChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
