using System.Reflection;
using Grepdesk.Cli.Commands;
using Grepdesk.Core;

namespace Grepdesk.Cli;

public static class CliRunner
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        TextReader? stdin = null,
        IPlatformShell? platformShell = null,
        IDriveKindProvider? driveKindProvider = null,
        bool? isInteractive = null,
        bool showProgress = false,
        CancellationToken ct = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        stdin ??= Console.In;
        platformShell ??= CliPlatformFactory.CreatePlatformShell();
        driveKindProvider ??= CliPlatformFactory.CreateDriveKindProvider();
        bool interactive = isInteractive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);

        if (args.Length == 0)
        {
            PrintMainHelp(stdout);
            return 0;
        }

        var subcommand = args[0].Trim().ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        try
        {
            return subcommand switch
            {
                "-h" or "--help" or "help" => PrintHelpAndReturn(stdout),
                "-v" or "--version" or "version" => PrintVersionAndReturn(stdout),
                "find" => await FindCommand.ExecuteAsync(subArgs, stdout, stderr, ct),
                "grep" => await GrepCommand.ExecuteAsync(subArgs, stdout, stderr, ct),
                "open" or "cat" or "read" or "view" => await OpenCommand.ExecuteAsync(subArgs, stdout, stderr, platformShell, ct),
                "zip" => await ZipCommand.ExecuteAsync(subArgs, stdout, stderr, driveKindProvider, showProgress, ct),
                "unzip" => await UnzipCommand.ExecuteAsync(subArgs, stdout, stderr, stdin, driveKindProvider, showProgress, interactive, ct),
                "duplicates" or "dup" or "dups" => await DuplicatesCommand.ExecuteAsync(subArgs, stdout, stderr, stdin, platformShell, ct),
                "du" => await DuCommand.ExecuteAsync(subArgs, stdout, stderr, ct),
                _ => FailUnknownCommand(stderr, args[0])
            };
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("Operation cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"Error: {ex.Message}");
            return 2;
        }
    }

    public static string GetVersionString()
    {
        var asm = typeof(CliRunner).Assembly;
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(infoVer))
        {
            var plus = infoVer.IndexOf('+');
            return plus > 0 ? infoVer[..plus] : infoVer;
        }
        var ver = asm.GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.2.0";
    }

    private static int PrintHelpAndReturn(TextWriter stdout)
    {
        PrintMainHelp(stdout);
        return 0;
    }

    private static int PrintVersionAndReturn(TextWriter stdout)
    {
        stdout.WriteLine($"grepdesk {GetVersionString()}");
        return 0;
    }

    private static int FailUnknownCommand(TextWriter stderr, string cmd)
    {
        stderr.WriteLine($"Error: Unknown command '{cmd}'. Run 'grepdesk --help' for usage.");
        return 2;
    }

    private static void PrintMainHelp(TextWriter stdout)
    {
        stdout.WriteLine($"""
            Grepdesk CLI v{GetVersionString()} - High-performance file search, archive, and disk utility

            Usage:
              grepdesk <command> [options]

            Commands:
              find <pattern>                Instant file and directory name search
              grep <query>                  Deep content search (.txt, code, .md, .docx, .xlsx, .ppt, .pptx, .pdf)
              open <path>                   Read file/folder content in CLI, or open in Grepdesk/OS app
              zip <output.zip> <inputs...>  Multi-core parallel zip compression
              unzip <archive.zip>           Multi-core parallel zip extraction
              duplicates [path]             Identify duplicate files by size and content hash
              du [path]                     Disk usage summary and largest space consumers

            Global Options:
              -h, --help                    Show help information
              -v, --version                 Show version information

            Run 'grepdesk <command> --help' for details on a specific command.
            """);
    }
}
