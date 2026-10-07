using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Grepdesk.Core;

/// <summary>User folders .NET has no SpecialFolder for.</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// The Downloads folder where the user actually has it. On Windows it can be
    /// moved (to D:\ for example), so it is asked from the shell rather than assumed
    /// to be under the profile.
    /// </summary>
    public static string Downloads()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pointer) == 0)
                {
                    try
                    {
                        if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
