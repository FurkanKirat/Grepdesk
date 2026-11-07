using System.Text;

namespace Grepdesk.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        bool showProgress = !Console.IsErrorRedirected && !Console.IsOutputRedirected;
        return await CliRunner.RunAsync(args, showProgress: showProgress, ct: cts.Token);
    }
}
