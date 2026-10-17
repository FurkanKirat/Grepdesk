using Grepdesk.UI;

namespace Grepdesk.Tests;

public class SessionTrailTests
{
    private static readonly DateTime Boot = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

    private static void WriteTrail(string dir, int pid, DateTime startedUtc, params string[] steps) =>
        File.WriteAllLines(Path.Combine(dir, $"session-{pid}.trail"),
            steps.Prepend($"{pid} {startedUtc:O}"));

    [Fact]
    public void A_trail_left_by_a_process_that_is_gone_is_a_crash_and_is_taken_once()
    {
        using var dir = new TempDir();
        WriteTrail(dir.Path, 4242, Boot.AddHours(1), "10:00:00.000 viewer: open Pdf .pdf, 2 MB", "10:00:05.000 viewer: Maximized");

        var crashes = SessionTrail.TakeCrashes(dir.Path, isRunning: (_, _) => false, bootUtc: Boot);

        var crash = Assert.Single(crashes);
        Assert.Equal(4242, crash.ProcessId);
        Assert.Equal("10:00:05.000 viewer: Maximized", crash.Trail[^1]);
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Empty(SessionTrail.TakeCrashes(dir.Path, isRunning: (_, _) => false, bootUtc: Boot));
    }

    [Fact]
    public void A_running_process_keeps_its_trail()
    {
        using var dir = new TempDir();
        WriteTrail(dir.Path, 77, Boot.AddHours(1), "step");

        Assert.Empty(SessionTrail.TakeCrashes(dir.Path, isRunning: (_, _) => true, bootUtc: Boot));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    /// <summary>Shutting Windows down doesn't let processes exit normally; that isn't a crash.</summary>
    [Fact]
    public void A_trail_from_before_the_last_restart_is_dropped_quietly()
    {
        using var dir = new TempDir();
        WriteTrail(dir.Path, 77, Boot.AddHours(-3), "step");

        Assert.Empty(SessionTrail.TakeCrashes(dir.Path, isRunning: (_, _) => false, bootUtc: Boot));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Windows_crash_record_names_the_faulting_module_for_that_process()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/><EventID>1000</EventID></System><EventData><Data Name='AppName'>Other.exe</Data><Data Name='ModuleName'>other.dll</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='ProcessId'>0x10</Data></EventData></Event>
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/><EventID>1000</EventID></System><EventData><Data Name='AppName'>Grepdesk.UI.exe</Data><Data Name='ModuleName'>pdfium.dll</Data><Data Name='ModuleVersion'>1.0.0.0</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='FaultingOffset'>00000000001a2b3c</Data><Data Name='ProcessId'>0x1092</Data><Data Name='ModulePath'>C:\app\pdfium.dll</Data></EventData></Event>
            """;

        var report = SessionTrail.ParseEvents(xml, 0x1092);

        Assert.NotNull(report);
        Assert.Contains("pdfium.dll", report);
        Assert.Contains("access violation", report);
        Assert.DoesNotContain("other.dll", report);
    }
}
