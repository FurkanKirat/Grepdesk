using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Grepdesk.Benchmarks;
using Grepdesk.Core;
using Grepdesk.Windows;

// Grepdesk benchmark runner: times Grepdesk's engines against Explorer,
// 7-Zip, robocopy and single-threaded .NET on generated datasets.
//
//   dotnet run -c Release --project Grepdesk.Benchmarks -- [options]
//
//   --work <dir>        where datasets and outputs live (default: %TEMP%\grepdesk-bench)
//   --other <dir>       also time copying to this folder, e.g. on another drive
//   --scale <n>         dataset size multiplier (default 1; 0.1 for a quick smoke run)
//   --rounds <n>        runs per competitor; the median is reported (default 3)
//   --warmup <n>        untimed runs per competitor first (default 1)
//   --datasets <a,b>    source-code, media, large (default: all)
//   --ops <a,b>         compress, extract, copy (default: all)
//   --only <a,b>        competitor names, e.g. Grepdesk,Explorer (default: all available)
//   --workers <n>       force Grepdesk's worker count (default: chosen per drive type)
//   --copier stream     copy with the portable stream copier instead of the native one

var options = Options.Parse(args);
Competitor.WorkerOverride = options.Workers;
Competitor.UseStreamCopier = args.SkipWhile(a => a != "--copier").Skip(1).FirstOrDefault() == "stream";
Directory.CreateDirectory(options.Work);
var results = new List<Result>();

Console.WriteLine(MachineInfo(options));
Console.WriteLine();

foreach (var dataset in Dataset.All.Where(d => options.Datasets is null || options.Datasets.Contains(d.Name)))
{
    Console.Write($"Preparing {dataset.Name} (scale {options.Scale})... ");
    var source = dataset.Ensure(options.Work, options.Scale);
    var expected = TreeStats.Of(source);
    Console.WriteLine(expected);

    // Every extractor reads the same archive, made by the neutral single-threaded .NET writer.
    var archive = Path.Combine(options.Work, "data", $"{dataset.Name}-x{options.Scale:0.###}.zip");
    if (!File.Exists(archive))
        ZipFile.CreateFromDirectory(source, archive, CompressionLevel.Optimal, includeBaseDirectory: true);

    // Whoever reads freshly written files first also pays for the antivirus
    // scan and the cache misses; read everything once so no competitor does.
    ReadAll(source);
    ReadAll(archive);

    var targets = new List<(string Label, string Root)> { ("", Path.Combine(options.Work, "runs")) };
    if (options.Other is not null)
        targets.Add((" → other drive", Path.Combine(options.Other, "runs")));

    foreach (var operation in options.Operations)
    {
        foreach (var (label, root) in operation == Operation.Copy ? targets : targets.Take(1))
        {
            var title = $"{dataset.Name} · {operation}{label}";
            Console.WriteLine($"  {title}");

            foreach (var competitor in Competitor.All.Where(c => c.Operation == operation && c.IsAvailable()
                         && (options.Only is null || options.Only.Contains(c.Name, StringComparer.OrdinalIgnoreCase))))
            {
                var result = await Measure(competitor, operation, source, archive, expected, root, options.Warmup, options.Rounds);
                results.Add(result with { Dataset = dataset.Name, Scenario = title });
                Console.WriteLine($"    {competitor.Name,-28} {result.Summary}");
            }
        }
    }
    Console.WriteLine();
}

var report = Report(results, options);
var reportPath = Path.Combine(options.Work, $"results-{DateTime.Now:yyyyMMdd-HHmmss}.md");
File.WriteAllText(reportPath, report);
Console.WriteLine(report);
Console.WriteLine($"Saved: {reportPath}");
return results.Any(r => r.Failure is not null) ? 1 : 0;

// ------------------------------------------------------------------ runner

static async Task<Result> Measure(Competitor competitor, Operation operation, string source, string archive,
    TreeStats expected, string runsRoot, int warmup, int rounds)
{
    var times = new List<double>();
    // Warm-up rounds run and are verified like the rest, but not timed:
    // they absorb JIT (Grepdesk runs in-process) and first-touch costs.
    for (var round = 1 - warmup; round <= rounds; round++)
    {
        var slug = string.Concat(competitor.Name.Where(char.IsLetterOrDigit));
        var output = operation == Operation.Compress
            ? Path.Combine(runsRoot, $"{slug}-{round}.zip")
            : Path.Combine(runsRoot, $"{slug}-{round}");
        Clean(output);
        Directory.CreateDirectory(runsRoot);

        var input = new BenchInput(operation == Operation.Extract ? archive : source, output, expected);
        var clock = Stopwatch.StartNew();
        try
        {
            await competitor.Run(input);
        }
        catch (Exception ex)
        {
            Clean(output);
            return new Result(competitor.Name, times, $"{ex.GetType().Name}: {ex.Message}");
        }
        clock.Stop();

        // Timing first, verification after: checking isn't part of the job.
        var problem = Competitor.Verify(operation, input);
        Clean(output);
        if (problem is not null)
            return new Result(competitor.Name, times, problem);

        if (round >= 1)
            times.Add(clock.Elapsed.TotalSeconds);
    }
    return new Result(competitor.Name, times, null);
}

static void ReadAll(string path)
{
    var buffer = new byte[1 << 20];
    var files = File.Exists(path) ? [path] : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
    foreach (var file in files)
    {
        using var stream = File.OpenRead(file);
        while (stream.Read(buffer) > 0) { }
    }
}

static void Clean(string path)
{
    if (File.Exists(path)) File.Delete(path);
    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
}

// ------------------------------------------------------------------ report

static string Report(List<Result> results, Options options)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Grepdesk benchmark");
    sb.AppendLine();
    sb.AppendLine(MachineInfo(options).Replace("\n", "  \n"));
    sb.AppendLine();
    sb.AppendLine($"Median of {options.Rounds} runs after {options.Warmup} untimed warm-up run(s), warm file cache. Explorer times are polled every " +
                  $"{ExplorerTimeoutNote()} and carry that much error. \"vs Explorer\" > 1 means faster than Explorer.");

    foreach (var scenario in results.GroupBy(r => r.Scenario))
    {
        var explorer = scenario.FirstOrDefault(r => r.Competitor == Competitor.ExplorerName && r.Failure is null)?.Median;
        sb.AppendLine();
        sb.AppendLine($"## {scenario.Key}");
        sb.AppendLine();
        sb.AppendLine("| Tool | Median (s) | Best (s) | vs Explorer |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var r in scenario.OrderBy(r => r.Failure is null ? r.Median : double.MaxValue))
        {
            if (r.Failure is not null)
            {
                sb.AppendLine($"| {r.Competitor} | failed | | {r.Failure} |");
                continue;
            }
            var ratio = explorer is { } e && r.Median > 0 ? $"{e / r.Median:0.0}×" : "";
            var name = r.Competitor == Competitor.GrepdeskName ? $"**{r.Competitor}**" : r.Competitor;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {r.Median:0.00} | {r.Best:0.00} | {ratio} |"));
        }
    }
    return sb.ToString();
}

static string ExplorerTimeoutNote() =>
    OperatingSystem.IsWindows() ? $"{ExplorerShell.PollInterval.TotalMilliseconds:0} ms" : "n/a";

static string MachineInfo(Options options)
{
    var cpu = "unknown CPU";
    if (OperatingSystem.IsWindows())
        cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? cpu;
    else if (File.Exists("/proc/cpuinfo"))
        cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? cpu;

    IDriveKindProvider drives = OperatingSystem.IsWindows() ? new WindowsDriveKindProvider() : new BasicDriveKindProvider();
    var lines = new List<string>
    {
        $"{RuntimeInformation.OSDescription} · {cpu.Trim()} · {Environment.ProcessorCount} logical cores · .NET {Environment.Version}",
        $"Work: {options.Work} ({drives.GetKind(options.Work)})"
    };
    if (options.Other is not null)
        lines.Add($"Other: {options.Other} ({drives.GetKind(options.Other)})");
    return string.Join('\n', lines);
}

// ------------------------------------------------------------------ types

sealed record Result(string Competitor, List<double> Times, string? Failure)
{
    public string Dataset { get; init; } = "";
    public string Scenario { get; init; } = "";
    public double Median => Times.Count == 0 ? 0 : Times.Order().ElementAt(Times.Count / 2);
    public double Best => Times.Count == 0 ? 0 : Times.Min();
    public string Summary => Failure is not null ? $"FAILED: {Failure}" : $"median {Median:0.00} s  (best {Best:0.00} s)";
}

sealed record Options(string Work, string? Other, double Scale, int Warmup, int Rounds, HashSet<string>? Datasets, List<Operation> Operations, HashSet<string>? Only, int? Workers)
{
    public static Options Parse(string[] args)
    {
        string Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
                                     ?? throw new ArgumentException($"{name} needs a value");
        bool Has(string name) => args.Contains(name);
        HashSet<string>? List(string name) => Has(name) ? Value(name).Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var ops = List("--ops");
        return new Options(
            Has("--work") ? Path.GetFullPath(Value("--work")) : Path.Combine(Path.GetTempPath(), "grepdesk-bench"),
            Has("--other") ? Path.GetFullPath(Value("--other")) : null,
            Has("--scale") ? double.Parse(Value("--scale"), CultureInfo.InvariantCulture) : 1,
            Has("--warmup") ? int.Parse(Value("--warmup")) : 1,
            Has("--rounds") ? int.Parse(Value("--rounds")) : 3,
            List("--datasets"),
            Enum.GetValues<Operation>().Where(o => ops is null || ops.Contains(o.ToString())).ToList(),
            List("--only"),
            Has("--workers") ? int.Parse(Value("--workers")) : null);
    }
}
