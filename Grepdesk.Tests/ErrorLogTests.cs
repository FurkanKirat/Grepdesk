using Grepdesk.UI;

namespace Grepdesk.Tests;

public class ErrorLogTests
{
    [Fact]
    public void Errors_are_appended_to_a_daily_file()
    {
        using var dir = new TempDir();

        var first = ErrorLog.Write(new InvalidOperationException("first"), "UI thread", dir.Path);
        var second = ErrorLog.Write(new IOException("second"), "unobserved task", dir.Path);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        var text = File.ReadAllText(first);
        Assert.Contains("UI thread", text);
        Assert.Contains("System.InvalidOperationException: first", text);
        Assert.Contains("System.IO.IOException: second", text);
    }

    [Fact]
    public void Only_the_newest_files_are_kept()
    {
        using var dir = new TempDir();
        for (var day = 1; day <= 15; day++)
            dir.WriteFile($"grepdesk-2026-01-{day:00}.log", "old");

        ErrorLog.Write(new Exception("now"), "test", dir.Path);

        var files = Directory.GetFiles(dir.Path, "grepdesk-*.log").Select(Path.GetFileName).ToList();
        Assert.Equal(10, files.Count);
        Assert.DoesNotContain("grepdesk-2026-01-01.log", files);
        Assert.Contains($"grepdesk-{DateTime.Now:yyyy-MM-dd}.log", files);
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_throw()
    {
        using var dir = new TempDir();
        var blocker = dir.WriteFile("logs", "a file where the folder should be");

        Assert.Null(ErrorLog.Write(new Exception("x"), "test", blocker));
    }

    [Fact]
    public void Internal_build_number_is_present_and_logged()
    {
        Assert.True(AppInfo.InternalBuild >= 1);
        Assert.Equal("0.2.0 #3 (abc1234)", AppInfo.FormatDisplay("0.2.0", 3, "abc1234"));
        Assert.Equal("0.2.0 #3", AppInfo.FormatDisplay("0.2.0", 3, null));
        Assert.Equal("0.2.0 (abc1234)", AppInfo.FormatDisplay("0.2.0", 0, "abc1234"));

        using var dir = new TempDir();
        var path = ErrorLog.Write("test", "details", dir.Path);

        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains($"#{AppInfo.InternalBuild}", text);
    }
}
