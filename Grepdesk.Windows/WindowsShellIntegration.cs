using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Grepdesk.Core;
using Microsoft.Win32;

namespace Grepdesk.Windows;

/// <summary>
/// Explorer context-menu entries as classic registry verbs under HKCU
/// (no admin rights, current user only). On Windows 11 they appear under
/// "Show more options".
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
            new(@"Directory\shell", "Grepdesk", "MenuOpenWith", "\"%1\"")
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

    private static string KeyPath(Verb verb) => $@"{Classes}{verb.ParentKey}\{verb.Name}";

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
            return ShellActionResult.Success(null);
        }
        catch (Exception ex)
        {
            return ShellActionResult.Failure(ShellActionStatus.OperationFailed, ex);
        }
    }
}
