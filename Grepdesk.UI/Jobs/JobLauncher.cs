using System.Diagnostics;

namespace Grepdesk.UI.Jobs;

/// <summary>
/// Starts a zip, extract or paste job the way Explorer's menu entries do: a
/// Grepdesk process of its own with a progress window, so a long job never
/// blocks the window it was started from.
/// </summary>
internal static class JobLauncher
{
    /// <param name="flag">An <see cref="Startup.AppCommand"/> flag, e.g. <c>--compress</c>.</param>
    /// <returns>Why the process couldn't be started, or null.</returns>
    public static Exception? Start(string flag, IEnumerable<string> paths)
    {
        if (Environment.ProcessPath is not { } exe) return new InvalidOperationException("Unknown executable path");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add(flag);
            foreach (var path in paths) psi.ArgumentList.Add(path);
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
