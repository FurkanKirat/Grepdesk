using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Grepdesk.Core.Archive;

namespace Grepdesk.Tests;

public class ZipWriterTests
{
    /// <summary>Reads an entry's compression method from the central directory (ZipArchive doesn't expose it).</summary>
    internal static ushort MethodOf(string zipPath, string entryName)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var name = Encoding.UTF8.GetBytes(entryName);
        for (var i = 0; i < bytes.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x02014b50)
                continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28));
            if (bytes.AsSpan(i + 46, nameLength).SequenceEqual(name))
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 10));
        }
        throw new InvalidOperationException($"{entryName} not found");
    }

    [Fact]
    public void More_than_65535_entries_uses_zip64_end_records()
    {
        using var output = new MemoryStream();
        var writer = new ZipWriter(output);
        var crc = Crc32.HashToUInt32("x"u8);
        for (var i = 0; i < 70_000; i++)
            writer.AddPrepared($"f{i}.txt", DateTime.Now, FileAttributes.Normal, ZipWriter.MethodStored, crc, 1, 1, s => s.WriteByte((byte)'x'));
        writer.Finish();

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(70_000, archive.Entries.Count);
        using var reader = new StreamReader(archive.GetEntry("f69999.txt")!.Open());
        Assert.Equal("x", reader.ReadToEnd());
    }

    [Fact]
    public void Entry_over_4GB_and_offsets_past_4GB_round_trip()
    {
        const long huge = 4L * 1024 * 1024 * 1024 + 12_345; // 4 GiB + a bit: overflows every 32-bit field
        using var output = new SparseStream();
        var writer = new ZipWriter(output);

        writer.AddStoredStream("huge.bin", DateTime.Now, FileAttributes.Normal, new ZeroStream(huge), huge, _ => { }, () => { });
        // This entry's local header starts past 4 GiB, so its offset needs Zip64 too.
        var small = "after the giant"u8.ToArray();
        writer.AddPrepared("small.txt", DateTime.Now, FileAttributes.Normal, ZipWriter.MethodStored,
            Crc32.HashToUInt32(small), small.Length, small.Length, s => s.Write(small));
        writer.Finish();

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        var entry = archive.GetEntry("huge.bin")!;
        Assert.Equal(huge, entry.Length);

        // The CRC is patched in after streaming; check it's the real one.
        var expected = new Crc32();
        var zeros = new byte[1 << 20];
        for (var left = huge; left > 0; left -= zeros.Length)
            expected.Append(zeros.AsSpan(0, (int)Math.Min(zeros.Length, left)));
        Assert.Equal(expected.GetCurrentHashAsUInt32(), entry.Crc32);

        using var reader = new StreamReader(archive.GetEntry("small.txt")!.Open());
        Assert.Equal("after the giant", reader.ReadToEnd());
    }

    [Fact]
    public void Non_ascii_names_are_flagged_utf8()
    {
        using var output = new MemoryStream();
        var writer = new ZipWriter(output);
        writer.AddDirectory("klasör", DateTime.Now);
        writer.AddPrepared("klasör/ığüşöç.txt", DateTime.Now, FileAttributes.Normal, ZipWriter.MethodStored, 0, 0, 0, _ => { });
        writer.Finish();

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: Encoding.Latin1);
        // With the UTF-8 flag set, the reader ignores the fallback encoding.
        Assert.Equal(["klasör/", "klasör/ığüşöç.txt"], archive.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void Dos_time_clamps_to_the_formats_range_and_halves_seconds()
    {
        Assert.Equal(ZipWriter.ToDos(new DateTime(1980, 1, 1)), ZipWriter.ToDos(new DateTime(1970, 6, 1)));
        var (time, _) = ZipWriter.ToDos(new DateTime(2024, 1, 1, 13, 45, 59));
        Assert.Equal((13 << 11) | (45 << 5) | 29, time);
    }

    /// <summary>Reads as a run of zero bytes without allocating them.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A seekable in-memory stream that only stores pages holding non-zero bytes, so multi-GB archives of zeros fit in RAM.</summary>
    private sealed class SparseStream : Stream
    {
        private const int PageSize = 64 * 1024;
        private readonly Dictionary<long, byte[]> _pages = [];
        private long _length;

        public override long Position { get; set; }
        public override long Length => _length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var page = Position / PageSize;
                var at = (int)(Position % PageSize);
                var n = Math.Min(PageSize - at, data.Length);
                var chunk = data[..n];
                if (_pages.TryGetValue(page, out var existing))
                    chunk.CopyTo(existing.AsSpan(at));
                else if (chunk.ContainsAnyExcept((byte)0))
                    chunk.CopyTo((_pages[page] = new byte[PageSize]).AsSpan(at));
                Position += n;
                data = data[n..];
                _length = Math.Max(_length, Position);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            var total = (int)Math.Min(destination.Length, Math.Max(0, _length - Position));
            var done = 0;
            while (done < total)
            {
                var page = Position / PageSize;
                var at = (int)(Position % PageSize);
                var n = Math.Min(PageSize - at, total - done);
                if (_pages.TryGetValue(page, out var bytes))
                    bytes.AsSpan(at, n).CopyTo(destination[done..]);
                else
                    destination.Slice(done, n).Clear();
                Position += n;
                done += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => _length + offset
        };

        public override void SetLength(long value) => _length = value;
        public override void Flush() { }
    }
}

/// <summary>
/// ZipCompressor compresses large files in parallel chunks and relies on this
/// .NET behaviour: a sync-flushed chunk ends byte-aligned without a final
/// block, so the chunks concatenate into one valid deflate stream.
/// </summary>
public class DeflateChunkingTests
{
    [Fact]
    public void Sync_flushed_chunks_concatenate_into_one_valid_stream()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 200_000).Select(i => $"line {i} value {i * 7 % 13}\n")));
        var output = new MemoryStream();
        const int chunk = 1 << 20;
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            var part = new MemoryStream();
            var deflate = new DeflateStream(part, CompressionLevel.Optimal, leaveOpen: true);
            deflate.Write(data, offset, Math.Min(chunk, data.Length - offset));
            var last = offset + chunk >= data.Length;
            if (last) deflate.Dispose(); else deflate.Flush();
            var length = part.Length;
            if (!last) deflate.Dispose();
            output.Write(part.GetBuffer(), 0, (int)length);
        }
        output.Position = 0;
        using var inflate = new DeflateStream(output, CompressionMode.Decompress);
        var roundTrip = new MemoryStream();
        inflate.CopyTo(roundTrip);
        Assert.Equal(data, roundTrip.ToArray());
    }
}
