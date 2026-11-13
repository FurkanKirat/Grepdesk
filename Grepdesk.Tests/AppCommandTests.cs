using Grepdesk.UI.Startup;

namespace Grepdesk.Tests;

/// <summary>
/// Arguments as they reach Main after Windows splits Explorer's command line
/// (e.g. <c>--paste "%V"</c> with %V = <c>D:\</c>).
/// </summary>
public class AppCommandTests
{
    [WindowsTheory]
    // Drive root: "D:\" on the command line arrives as D:" (the \" is read as an escaped quote).
    [InlineData("D:\"", @"D:\")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("D:", @"D:\")]
    // Any folder path whose %V ends in a backslash hits the same rule.
    [InlineData("D:\\Projeler\\hedef\"", @"D:\Projeler\hedef")]
    [InlineData(@"C:\Users\x\Belgeler", @"C:\Users\x\Belgeler")]
    public void Explorer_paths_are_cleaned(string argument, string expected)
    {
        var command = AppCommand.Parse(["--paste", argument]);

        Assert.Equal(CommandKind.Paste, command.Kind);
        Assert.Equal(Path.TrimEndingDirectorySeparator(expected), Path.TrimEndingDirectorySeparator(command.Paths.Single()));
    }

    [WindowsFact]
    public void Flags_and_several_paths_are_parsed()
    {
        var command = AppCommand.Parse(["--compress", "--benchmark", @"C:\a", @"D:\b.txt"]);

        Assert.Equal(CommandKind.Compress, command.Kind);
        Assert.True(command.Benchmark);
        Assert.Equal([@"C:\a", @"D:\b.txt"], command.Paths);
    }

    [Fact]
    public void Drop_flags_carry_the_sources_and_the_destination_last()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gd"));
        var copy = AppCommand.Parse(["--copy-to", Path.Combine(root, "a.txt"), Path.Combine(root, "dest")]);
        var move = AppCommand.Parse(["--move-to", Path.Combine(root, "a.txt"), Path.Combine(root, "dest")]);

        Assert.Equal(CommandKind.Copy, copy.Kind);
        Assert.Equal(CommandKind.Move, move.Kind);
        Assert.True(copy.IsJob && move.IsJob);
        Assert.Equal(Path.Combine(root, "dest"), copy.Paths[^1]);
        Assert.Equal(2, move.Paths.Count);
    }

    [Fact]
    public void Plain_starts_distinguish_search_folder_and_file()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("note.txt", "hello");

        var search = AppCommand.Parse([]);
        var folder = AppCommand.Parse([dir.Path]);
        var openFile = AppCommand.Parse([file]);

        Assert.Equal(CommandKind.OpenSearch, search.Kind);
        Assert.Equal(CommandKind.OpenFolder, folder.Kind);
        Assert.Equal(CommandKind.OpenFile, openFile.Kind);
        Assert.False(search.IsJob || folder.IsJob || openFile.IsJob);
        Assert.Equal(dir.Path, folder.Paths.Single());
        Assert.Equal(file, openFile.Paths.Single());
    }

    [Fact]
    public void Survives_the_pipe_round_trip()
    {
        var command = AppCommand.Parse(["--extract-here", @"D:\arşiv klasörü\a.zip"]);
        Assert.Equal(command.Paths, AppCommand.Deserialize(command.Serialize())!.Paths);
        Assert.Equal(command.Kind, AppCommand.Deserialize(command.Serialize())!.Kind);
    }
}
