using System.IO.Compression;
using Grepdesk.Core.Browse;

namespace Grepdesk.Tests;

public class ZipFolderListingTests
{
    private static string CreateTestZip(TempDir tmp)
    {
        var zipPath = tmp.Combine("bundle.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void AddText(string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        AddText("readme.txt", "hello");
        AddText("src/app.cs", "class App {}");
        AddText("src/deep/helper.cs", "class Helper {}");
        return zipPath;
    }

    [Fact]
    public void TrySplit_distinguishes_zip_root_and_nested_folder()
    {
        using var tmp = new TempDir();
        var zipPath = CreateTestZip(tmp);

        Assert.True(ZipBrowsePath.TrySplit(zipPath, out var a1, out var i1));
        Assert.Equal(zipPath, a1);
        Assert.Equal("", i1);

        var nested = Path.Combine(zipPath, "src", "deep");
        Assert.True(ZipBrowsePath.TrySplit(nested, out var a2, out var i2));
        Assert.Equal(zipPath, a2);
        Assert.Equal("src/deep", i2);

        Assert.False(ZipBrowsePath.TrySplit(tmp.Combine("missing.zip"), out _, out _));
    }

    [Fact]
    public void List_returns_virtual_folders_and_files_with_sizes()
    {
        using var tmp = new TempDir();
        var zipPath = CreateTestZip(tmp);

        var root = ZipFolderListing.List(zipPath);
        Assert.True(root.IsReadable);
        Assert.Equal(2, root.Entries.Count);

        var srcDir = root.Entries.Single(e => e.IsDirectory);
        Assert.Equal("src", srcDir.Name);
        Assert.Equal("class App {}".Length + "class Helper {}".Length, srcDir.Size);

        var readme = root.Entries.Single(e => !e.IsDirectory);
        Assert.Equal("readme.txt", readme.Name);
        Assert.Equal("hello".Length, readme.Size);

        var sub = ZipFolderListing.List(Path.Combine(zipPath, "src"));
        Assert.True(sub.IsReadable);
        Assert.Equal(2, sub.Entries.Count);
        Assert.Contains(sub.Entries, e => e.IsDirectory && e.Name == "deep");
        Assert.Contains(sub.Entries, e => !e.IsDirectory && e.Name == "app.cs");
    }

    [Fact]
    public void TryExtractEntryToTemp_extracts_entry_contents()
    {
        using var tmp = new TempDir();
        var zipPath = CreateTestZip(tmp);
        var virtualFile = Path.Combine(zipPath, "src", "deep", "helper.cs");

        var tempRoot = tmp.Combine("extract-cache");
        var extracted = ZipFolderListing.TryExtractEntryToTemp(virtualFile, tempRoot);

        Assert.NotNull(extracted);
        Assert.True(File.Exists(extracted));
        Assert.Equal("class Helper {}", File.ReadAllText(extracted));
    }

    [Fact]
    public void AddressResolver_resolves_zip_root_and_inner_paths()
    {
        using var tmp = new TempDir();
        var zipPath = CreateTestZip(tmp);

        var rootTarget = AddressResolver.Resolve(zipPath, null);
        Assert.Equal(AddressKind.Folder, rootTarget.Kind);

        var dirTarget = AddressResolver.Resolve(Path.Combine(zipPath, "src"), null);
        Assert.Equal(AddressKind.Folder, dirTarget.Kind);

        var fileTarget = AddressResolver.Resolve(Path.Combine(zipPath, "src", "app.cs"), null);
        Assert.Equal(AddressKind.File, fileTarget.Kind);

        var segments = AddressResolver.Segments(Path.Combine(zipPath, "src", "deep"));
        Assert.Equal("deep", segments[^1].Label);
        Assert.Equal("bundle.zip", segments[^3].Label);
    }
}
