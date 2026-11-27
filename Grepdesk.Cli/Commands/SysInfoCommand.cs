using System.Text.Json;
using Grepdesk.Core.SystemInfo;

namespace Grepdesk.Cli.Commands;

internal static class SysInfoCommand
{
    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        ISystemInfoProvider provider,
        CancellationToken ct)
    {
        bool detailed = false;
        bool jsonOutput = false;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp(stdout);
                    return 0;

                case "-d":
                case "--details":
                case "--full":
                    detailed = true;
                    break;

                case "--json":
                    jsonOutput = true;
                    break;

                default:
                    await stderr.WriteLineAsync($"Error: Unknown option '{arg}'. Run 'grepdesk sysinfo --help' for usage.");
                    return 2;
            }
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = provider.GetSystemInfo();

        if (jsonOutput)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(snapshot, JsonIndented));
        }
        else
        {
            await stdout.WriteLineAsync(SystemInfoFormatter.FormatPlainText(snapshot, detailed));
        }

        return 0;
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage:
              grepdesk sysinfo [options]

            Options:
              -d, --details, --full   Show detailed hardware specifications (clocks, cache, RAM sticks, drivers, BIOS)
              --json                  Output structured JSON
              -h, --help              Show help information
            """);
    }
}
