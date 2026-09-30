using System.Diagnostics;
using System.IO.Compression;
using Grepdesk.Benchmarks;

namespace Grepdesk.Tests;

/// <summary>Shared, generated-once datasets for the benchmark tests.</summary>
public sealed class BenchmarkData : IDisposable
{
    private readonly TempDir _work = new();

    public string Work => _work.Path;

    public (string Source, string Archive, TreeStats Expected) Get(Dataset dataset, double scale)
    {
        var source = dataset.Ensure(Work, scale);
        var archive = Path.Combine(Work, $"{dataset.Name}-{scale}.zip");
        if (!File.Exists(archive))
            ZipFile.CreateFromDirectory(source, archive, CompressionLevel.Optimal, includeBaseDirectory: true);
        return (source, archive, TreeStats.Of(source));
    }

    public string Output(string name)
    {
        var runs = Directory.CreateDirectory(Path.Combine(Work, "runs")).FullName;
        return Path.Combine(runs, $"{name}-{Guid.NewGuid():N}");
    }

    public void Dispose() => _work.Dispose();
}

/// <summary>
/// Keeps the benchmark runner honest: every competitor it times must produce
/// the complete dataset, otherwise its numbers mean nothing. Tiny datasets, so
/// these run with the normal test suite. (Explorer is left out here: it opens
/// progress windows; the performance tests below cover it.)
/// </summary>
public class BenchmarkHarnessTests(BenchmarkData data) : IClassFixture<BenchmarkData>
{
    public static TheoryData<string> NonExplorerCompetitors() =>
        [.. Competitor.All.Where(c => c.Name != Competitor.ExplorerName).Select(c => $"{c.Operation}:{c.Name}")];

    [Theory]
    [MemberData(nameof(NonExplorerCompetitors))]
    public async Task Competitor_produces_the_whole_dataset(string id)
    {
        var competitor = Competitor.All.Single(c => $"{c.Operation}:{c.Name}" == id);
        if (!competitor.IsAvailable())
            return; // e.g. 7-Zip not installed

        foreach (var dataset in Dataset.All)
        {
            var (source, archive, expected) = data.Get(dataset, scale: 0.01);
            var output = data.Output("out") + (competitor.Operation == Operation.Compress ? ".zip" : "");

            var input = new BenchInput(competitor.Operation == Operation.Extract ? archive : source, output, expected);
            await competitor.Run(input);

            Assert.Null(Competitor.Verify(competitor.Operation, input));
        }
    }

    [Fact]
    public void Datasets_are_deterministic()
    {
        using var other = new TempDir();
        foreach (var dataset in Dataset.All)
        {
            var a = TreeStats.Of(dataset.Ensure(data.Work, 0.01));
            var b = TreeStats.Of(dataset.Ensure(other.Path, 0.01));
            Assert.Equal(a, b);
            Assert.True(a.Files > 0);
        }
    }
}

/// <summary>
/// Performance guards: Grepdesk must stay clearly faster than the
/// single-threaded .NET baseline (and than Explorer) on the workloads it
/// exists for. Timing tests are sensitive to machine load, so they only run
/// when asked for:
///
///   $env:GREPDESK_PERF = "1"; dotnet test -c Release --filter Category=Performance
///
/// The required speed-ups are well below what was measured on an 8-core+
/// NVMe machine (2.9×, 3.5×, 1.8×, 45×), so a failure means a real regression.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests(BenchmarkData data) : IClassFixture<BenchmarkData>
{
    private const double Scale = 0.3;
    private static bool Enabled => Environment.GetEnvironmentVariable("GREPDESK_PERF") == "1";

    private async Task<double> Median(Competitor competitor, Dataset dataset, int rounds = 3)
    {
        var (source, archive, expected) = data.Get(dataset, Scale);
        var times = new List<double>();

        for (var round = 0; round <= rounds; round++) // round 0 is an untimed warm-up
        {
            var output = data.Output(competitor.Name) + (competitor.Operation == Operation.Compress ? ".zip" : "");
            var input = new BenchInput(competitor.Operation == Operation.Extract ? archive : source, output, expected);

            var clock = Stopwatch.StartNew();
            await competitor.Run(input);
            clock.Stop();

            Assert.Null(Competitor.Verify(competitor.Operation, input));
            if (round > 0)
                times.Add(clock.Elapsed.TotalSeconds);
        }
        return times.Order().ElementAt(times.Count / 2);
    }

    private async Task AssertFaster(Competitor grepdesk, Competitor baseline, Dataset dataset, double atLeast)
    {
        if (!Enabled)
            return;
        var ours = await Median(grepdesk, dataset);
        var theirs = await Median(baseline, dataset);
        Assert.True(theirs / ours >= atLeast,
            $"{dataset.Name} {grepdesk.Operation}: Grepdesk {ours:0.00} s vs {baseline.Name} {theirs:0.00} s " +
            $"= {theirs / ours:0.0}×, need ≥ {atLeast}×");
    }

    [Fact]
    public Task Compressing_many_small_files_beats_single_threaded() =>
        AssertFaster(Competitor.GrepdeskCompress, Competitor.DotNetCompress, Dataset.SourceCode, 1.5);

    [Fact]
    public Task Compressing_media_skips_recompression() =>
        AssertFaster(Competitor.GrepdeskCompress, Competitor.DotNetCompress, Dataset.Media, 5);

    [Fact]
    public Task Extracting_many_small_files_beats_single_threaded() =>
        AssertFaster(Competitor.GrepdeskExtract, Competitor.DotNetExtract, Dataset.SourceCode, 1.5);

    [Fact]
    public Task Copying_many_small_files_beats_sequential_copy() =>
        AssertFaster(Competitor.GrepdeskCopy, Competitor.DotNetCopy, Dataset.SourceCode, 1.2);

    [Fact]
    public async Task Beats_Explorer_on_every_operation()
    {
        if (!Enabled || !OperatingSystem.IsWindows())
            return;
        await AssertFaster(Competitor.GrepdeskCompress, Competitor.ExplorerCompress, Dataset.SourceCode, 2);
        await AssertFaster(Competitor.GrepdeskExtract, Competitor.ExplorerExtract, Dataset.SourceCode, 2);
        await AssertFaster(Competitor.GrepdeskCopy, Competitor.ExplorerCopy, Dataset.SourceCode, 2);
    }
}
