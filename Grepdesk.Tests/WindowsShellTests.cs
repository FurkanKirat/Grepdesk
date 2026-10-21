using Grepdesk.Core.Transfer;
using Grepdesk.Windows;

namespace Grepdesk.Tests;

public class WindowsShellTests
{
    [Fact]
    public async Task Icons_are_drawn_for_files_and_folders()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var file = dir.WriteFile("notes.txt", "x");
        var icons = new WindowsShellIcons();

        foreach (var path in new[] { file, dir.Path })
        {
            var icon = await icons.GetIconAsync(path, 32);
            Assert.NotNull(icon);
            Assert.Equal(32, icon.Width);
            Assert.Equal(32 * 32 * 4, icon.Pixels.Length);
        }
    }

    [Fact]
    public async Task A_cancelled_icon_request_completes_with_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var file = dir.WriteFile("notes.txt", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await new WindowsShellIcons().GetIconAsync(file, 32, cts.Token));
    }

    /// <summary>Replaces whatever is on the clipboard, so it only runs when asked for: GREPDESK_CLIPBOARD=1.</summary>
    [Fact]
    public void Files_put_on_the_clipboard_read_back_with_the_cut_flag()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GREPDESK_CLIPBOARD") != "1") return;

        using var dir = new TempDir();
        var a = dir.WriteFile("a.txt", "a");
        var b = dir.WriteFile("ğüş é.txt", "b");
        var clipboard = new Win32FileClipboard();

        Assert.True(clipboard.SetFiles(new ClipboardFiles([a, b], IsCut: true)));
        Assert.Equal(new ClipboardFiles([a, b], true).Paths, clipboard.GetFiles()!.Paths);
        Assert.True(clipboard.GetFiles()!.IsCut);

        Assert.True(clipboard.SetFiles(new ClipboardFiles([a], IsCut: false)));
        Assert.False(clipboard.GetFiles()!.IsCut);

        clipboard.Clear();
        Assert.Null(clipboard.GetFiles());
    }
}
