using Grepdesk.Core.Viewer;

namespace Grepdesk.Tests;

public class SiblingFilesTests
{
    [Fact]
    public void Steps_through_the_folder_in_natural_name_order_and_stops_at_the_ends()
    {
        using var dir = new TempDir();
        var one = dir.WriteFile("photo1.jpg", "x");
        var two = dir.WriteFile("photo2.jpg", "x");
        var ten = dir.WriteFile("photo10.jpg", "x");

        Assert.Equal(two, SiblingFiles.Neighbor(one, +1));
        Assert.Equal(ten, SiblingFiles.Neighbor(two, +1)); // "photo2" before "photo10"
        Assert.Equal(one, SiblingFiles.Neighbor(two, -1));
        Assert.Null(SiblingFiles.Neighbor(one, -1));
        Assert.Null(SiblingFiles.Neighbor(ten, +1));
    }

    [Fact]
    public void Skips_folders_and_hidden_files()
    {
        using var dir = new TempDir();
        var a = dir.WriteFile("a.txt", "x");
        Directory.CreateDirectory(Path.Combine(dir.Path, "b-folder"));
        var hidden = dir.WriteFile("c-hidden.txt", "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var d = dir.WriteFile("d.txt", "x");

        Assert.Equal(d, SiblingFiles.Neighbor(a, +1));
        Assert.Equal(a, SiblingFiles.Neighbor(d, -1));
    }

    [Fact]
    public void A_file_that_is_not_listed_or_a_zero_step_has_no_neighbor()
    {
        using var dir = new TempDir();
        var a = dir.WriteFile("a.txt", "x");

        Assert.Null(SiblingFiles.Neighbor(Path.Combine(dir.Path, "missing.txt"), +1));
        Assert.Null(SiblingFiles.Neighbor(a, 0));
    }
}
