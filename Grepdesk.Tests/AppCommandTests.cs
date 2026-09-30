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
    public void Survives_the_pipe_round_trip()
    {
        var command = AppCommand.Parse(["--extract-here", @"D:\arşiv klasörü\a.zip"]);
        Assert.Equal(command.Paths, AppCommand.Deserialize(command.Serialize())!.Paths);
        Assert.Equal(command.Kind, AppCommand.Deserialize(command.Serialize())!.Kind);
    }
}
