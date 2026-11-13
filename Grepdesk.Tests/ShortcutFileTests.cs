using System.Text;
using Grepdesk.Core.Browse;

namespace Grepdesk.Tests;

public class ShortcutFileTests
{
    /// <summary>A minimal .lnk with only a LinkInfo block holding a local base path (and optionally a suffix).</summary>
    private static byte[] LocalShortcut(string basePath, string suffix = "")
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        // Header: 0x4C bytes, flags = HasLinkInfo
        w.Write(0x4Cu);
        w.Write(new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 });
        w.Write(0x2u);
        w.Write(new byte[0x4C - 4 - 16 - 4]);

        var baseBytes = Encoding.Latin1.GetBytes(basePath + "\0");
        var suffixBytes = Encoding.Latin1.GetBytes(suffix + "\0");
        const int volumeIdOffset = 0x1C, volumeIdSize = 0x10;
        var baseOffset = volumeIdOffset + volumeIdSize;
        var suffixOffset = baseOffset + baseBytes.Length;
        var total = suffixOffset + suffixBytes.Length;

        w.Write((uint)total);
        w.Write(0x1Cu);                  // header size (no unicode strings)
        w.Write(0x1u);                   // VolumeIDAndLocalBasePath
        w.Write((uint)volumeIdOffset);
        w.Write((uint)baseOffset);
        w.Write(0u);                     // no network link
        w.Write((uint)suffixOffset);
        w.Write((uint)volumeIdSize);     // VolumeID: size, fixed drive, serial, label offset
        w.Write(3u);
        w.Write(0u);
        w.Write(0x10u);
        w.Write(baseBytes);
        w.Write(suffixBytes);
        return ms.ToArray();
    }

    [Fact]
    public void Reads_the_target_of_a_shortcut()
    {
        Assert.Equal(@"C:\Users\Me\Documents", ShortcutFile.Parse(LocalShortcut(@"C:\Users\Me\Documents")));
    }

    [Fact]
    public void Joins_the_path_suffix_to_the_base_path()
    {
        var target = ShortcutFile.Parse(LocalShortcut("base", "inner"));
        Assert.Equal(Path.Combine("base", "inner"), target);
    }

    [Fact]
    public void Resolves_only_shortcuts_to_existing_folders()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Target");
        Directory.CreateDirectory(folder);
        var file = dir.WriteFile("note.txt", "x");

        var toFolder = Path.Combine(dir.Path, "to-folder.lnk");
        File.WriteAllBytes(toFolder, LocalShortcut(folder));
        var toFile = Path.Combine(dir.Path, "to-file.lnk");
        File.WriteAllBytes(toFile, LocalShortcut(file));
        var toGone = Path.Combine(dir.Path, "to-gone.lnk");
        File.WriteAllBytes(toGone, LocalShortcut(Path.Combine(dir.Path, "missing")));

        Assert.Equal(folder, ShortcutFile.ResolveFolder(toFolder));
        Assert.Null(ShortcutFile.ResolveFolder(toFile));
        Assert.Null(ShortcutFile.ResolveFolder(toGone));
    }

    [Fact]
    public void Broken_or_foreign_files_give_no_target_and_never_throw()
    {
        using var dir = new TempDir();
        var text = dir.WriteFile("fake.lnk", "this is not a shortcut at all, just text padded out to be long enough to pass the size check....");
        var truncated = Path.Combine(dir.Path, "cut.lnk");
        File.WriteAllBytes(truncated, LocalShortcut(@"C:\x")[..0x50]);

        Assert.Null(ShortcutFile.ReadTarget(text));
        Assert.Null(ShortcutFile.ReadTarget(truncated));
        Assert.Null(ShortcutFile.ReadTarget(Path.Combine(dir.Path, "absent.lnk")));
        Assert.Null(ShortcutFile.Parse([]));
    }
}
