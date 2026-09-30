using System.Text;

namespace Grepdesk.Benchmarks;

/// <summary>File count and total size of a folder tree; also how every run's output is verified.</summary>
public readonly record struct TreeStats(int Files, long Bytes)
{
    public static TreeStats Of(string root)
    {
        if (!Directory.Exists(root))
            return default;
        var files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files++;
            bytes += file.Length;
        }
        return new TreeStats(files, bytes);
    }

    public override string ToString() => $"{Files:N0} files, {Bytes / 1024.0 / 1024.0:N0} MB";
}

/// <summary>
/// Deterministic test data (fixed random seed), generated once and cached
/// under the work folder. <paramref name="Scale"/> multiplies file counts and
/// sizes so a quick smoke run and a long, realistic run use the same shapes.
/// </summary>
public sealed record Dataset(string Name, string Description, Action<string, double> Generate)
{
    // Bump when a generator changes so cached copies are rebuilt.
    private const int Version = 1;

    public static readonly Dataset SourceCode = new("source-code",
        "Many small, compressible text files in nested folders (a code repo / node_modules)",
        (dir, scale) =>
        {
            var random = new Random(1);
            var text = TextBlock(random, 4 * 1024 * 1024);
            var count = (int)(10_000 * scale);
            for (var i = 0; i < count; i++)
            {
                var folder = Path.Combine(dir, $"pkg{i % 40}", $"lib{i % 7}", $"src{i % 3}");
                Directory.CreateDirectory(folder);
                var size = random.Next(1_000, 30_000);
                var start = random.Next(0, text.Length - size);
                File.WriteAllBytes(Path.Combine(folder, $"file{i}.cs"), text.AsSpan(start, size).ToArray());
            }
        });

    public static readonly Dataset Media = new("media",
        "Already-compressed files (photos, videos) plus a few documents",
        (dir, scale) =>
        {
            var random = new Random(2);
            Directory.CreateDirectory(Path.Combine(dir, "photos"));
            Directory.CreateDirectory(Path.Combine(dir, "videos"));
            Directory.CreateDirectory(Path.Combine(dir, "docs"));
            var buffer = new byte[64 * 1024 * 1024];
            random.NextBytes(buffer);

            for (var i = 0; i < (int)(200 * scale); i++)
                WriteSlice(Path.Combine(dir, "photos", $"IMG_{i:0000}.jpg"), buffer, random, random.Next(1_000_000, 6_000_000));
            for (var i = 0; i < Math.Max(1, (int)(6 * scale)); i++)
                WriteSlice(Path.Combine(dir, "videos", $"clip{i}.mp4"), buffer, random, random.Next(40_000_000, 64_000_000));

            var text = TextBlock(random, 1024 * 1024);
            for (var i = 0; i < (int)(100 * scale); i++)
                File.WriteAllBytes(Path.Combine(dir, "docs", $"note{i}.txt"), text.AsSpan(random.Next(0, 500_000), random.Next(2_000, 400_000)).ToArray());
        });

    public static readonly Dataset Large = new("large",
        "A few big files: one compressible log, one incompressible disk image",
        (dir, scale) =>
        {
            var random = new Random(3);
            var size = (long)(512L * 1024 * 1024 * scale);
            var text = TextBlock(random, 8 * 1024 * 1024);
            WriteRepeated(Path.Combine(dir, "server.log"), text, size);

            var noise = new byte[8 * 1024 * 1024];
            random.NextBytes(noise);
            WriteRepeated(Path.Combine(dir, "disk.img"), noise, size);
        });

    public static IReadOnlyList<Dataset> All { get; } = [SourceCode, Media, Large];

    /// <summary>Returns the dataset folder, generating it first if it isn't cached.</summary>
    public string Ensure(string workDir, double scale)
    {
        var dir = Path.Combine(workDir, "data", $"{Name}-x{scale:0.###}");
        var marker = Path.Combine(dir, ".complete");
        if (File.Exists(marker) && File.ReadAllText(marker) == Version.ToString())
            return Path.Combine(dir, Name);

        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Generate(Path.Combine(dir, Name), scale);
        File.WriteAllText(marker, Version.ToString());
        return Path.Combine(dir, Name);
    }

    // Pseudo source code: realistic compression ratio, unlike repeated
    // characters (which compress absurdly well) or random bytes (not at all).
    private static byte[] TextBlock(Random random, int length)
    {
        string[] words =
        [
            "public", "private", "static", "void", "return", "if", "else", "var", "new", "class", "int", "string",
            "await", "async", "Task", "List", "count", "value", "index", "result", "config", "request", "response",
            "user", "file", "path", "buffer", "stream", "=", "==", "{", "}", "(", ")", ";", "=>", "null", "true", "false"
        ];
        var sb = new StringBuilder(length + 64);
        while (sb.Length < length)
        {
            sb.Append(' ', random.Next(0, 4) * 4);
            var n = random.Next(3, 12);
            for (var i = 0; i < n; i++)
            {
                sb.Append(words[random.Next(words.Length)]);
                if (random.Next(5) == 0) sb.Append(random.Next(1000));
                sb.Append(' ');
            }
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString(0, length));
    }

    private static void WriteSlice(string path, byte[] source, Random random, int size)
    {
        using var file = File.Create(path);
        var left = size;
        while (left > 0)
        {
            var n = Math.Min(left, source.Length - 1);
            var start = random.Next(0, source.Length - n);
            file.Write(source, start, n);
            left -= n;
        }
    }

    private static void WriteRepeated(string path, byte[] block, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        file.SetLength(size);
        for (long written = 0; written < size; written += block.Length)
            file.Write(block, 0, (int)Math.Min(block.Length, size - written));
    }
}
