using System.Buffers;
using System.IO.Hashing;
using System.Text;

namespace Grepdesk.Core.Archive;

/// <summary>
/// Writes the zip container format (PKWARE APPNOTE 6.3): local headers,
/// entry data, central directory and end records, with Zip64 wherever a
/// size, offset or entry count outgrows the classic 32/16-bit fields.
/// It never compresses anything itself — entries arrive already compressed
/// (by <see cref="ZipCompressor"/>'s workers), which is what lets
/// compression run in parallel while this class writes sequentially.
/// Entry order doesn't matter in a zip, so entries are written as they come.
/// </summary>
public sealed class ZipWriter
{
    public const ushort MethodStored = 0;
    public const ushort MethodDeflate = 8;

    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const ushort Zip64ExtraId = 0x0001;

    private const ushort VersionDefault = 20; // 2.0: deflate, folders
    private const ushort VersionZip64 = 45;   // 4.5: Zip64
    private const ushort FlagUtf8 = 0x0800;   // bit 11: names are UTF-8

    private const uint Max32 = uint.MaxValue;
    private const ushort Max16 = ushort.MaxValue;

    private sealed record CentralRecord(
        byte[] Name, bool Utf8, ushort Method, uint Crc, long CompressedSize, long UncompressedSize,
        long LocalHeaderOffset, ushort DosTime, ushort DosDate, uint ExternalAttributes);

    private readonly Stream _output;
    private readonly List<CentralRecord> _records = [];

    /// <param name="output">Must be seekable: streamed entries get their CRC patched in afterwards.</param>
    public ZipWriter(Stream output)
    {
        if (!output.CanSeek)
            throw new ArgumentException("The output stream must be seekable.", nameof(output));
        _output = output;
    }

    public int EntryCount => _records.Count;

    /// <summary>A folder entry ("name/"). Only needed for empty folders; others are implied by their files.</summary>
    public void AddDirectory(string name, DateTime lastWrite)
    {
        if (!name.EndsWith('/'))
            name += '/';
        WriteEntry(name, lastWrite, FileAttributes.Directory, MethodStored, 0, 0, 0, _ => { });
    }

    /// <summary>An entry whose data is already compressed (or stored) and whose CRC and sizes are known.</summary>
    /// <param name="writeData">Writes exactly <paramref name="compressedLength"/> bytes to the stream it is given.</param>
    public void AddPrepared(string name, DateTime lastWrite, FileAttributes attributes, ushort method,
        uint crc, long uncompressedLength, long compressedLength, Action<Stream> writeData)
    {
        WriteEntry(name, lastWrite, attributes, method, crc, uncompressedLength, compressedLength, writeData);
    }

    /// <summary>
    /// Stores a file without compression, streaming it straight from the
    /// source. The CRC is computed on the way through and patched into the
    /// local header afterwards, so the file is read only once.
    /// </summary>
    public void AddStoredStream(string name, DateTime lastWrite, FileAttributes attributes, Stream source, long length,
        Action<long> onBytes, Action checkpoint)
    {
        var headerOffset = _output.Position;
        WriteEntry(name, lastWrite, attributes, MethodStored, crc: 0, length, length, output =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                var crc = new Crc32();
                long copied = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    crc.Append(buffer.AsSpan(0, read));
                    output.Write(buffer, 0, read);
                    copied += read;
                    onBytes(read);
                    checkpoint();
                }

                if (copied != length)
                    throw new IOException($"{name} changed size while it was being compressed.");

                var value = crc.GetCurrentHashAsUInt32();
                var end = output.Position;
                output.Position = headerOffset + 14; // CRC field of the local header
                WriteUInt32(output, value);
                output.Position = end;

                // The central record was created with CRC 0; fix it too.
                _records[^1] = _records[^1] with { Crc = value };
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });
    }

    private void WriteEntry(string name, DateTime lastWrite, FileAttributes attributes, ushort method,
        uint crc, long uncompressed, long compressed, Action<Stream> writeData)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var utf8 = nameBytes.Any(b => b >= 0x80);
        var (dosTime, dosDate) = ToDos(lastWrite);
        var offset = _output.Position;

        // The record is added before the data is written so AddStoredStream
        // can patch it; it is removed again if writing fails.
        _records.Add(new CentralRecord(nameBytes, utf8, method, crc, compressed, uncompressed, offset, dosTime, dosDate,
            (uint)attributes & 0xFF));

        try
        {
            var zip64 = uncompressed >= Max32 || compressed >= Max32;
            Span<byte> header = stackalloc byte[30];
            WriteUInt32(header, 0, LocalHeaderSignature);
            WriteUInt16(header, 4, zip64 ? VersionZip64 : VersionDefault);
            WriteUInt16(header, 6, utf8 ? FlagUtf8 : (ushort)0);
            WriteUInt16(header, 8, method);
            WriteUInt16(header, 10, dosTime);
            WriteUInt16(header, 12, dosDate);
            WriteUInt32(header, 14, crc);
            WriteUInt32(header, 18, zip64 ? Max32 : (uint)compressed);
            WriteUInt32(header, 22, zip64 ? Max32 : (uint)uncompressed);
            WriteUInt16(header, 26, (ushort)nameBytes.Length);
            WriteUInt16(header, 28, zip64 ? (ushort)20 : (ushort)0);
            _output.Write(header);
            _output.Write(nameBytes);

            if (zip64)
            {
                // In a local header the Zip64 extra always holds both sizes.
                Span<byte> extra = stackalloc byte[20];
                WriteUInt16(extra, 0, Zip64ExtraId);
                WriteUInt16(extra, 2, 16);
                WriteUInt64(extra, 4, (ulong)uncompressed);
                WriteUInt64(extra, 12, (ulong)compressed);
                _output.Write(extra);
            }

            var dataStart = _output.Position;
            writeData(_output);
            if (_output.Position - dataStart != compressed)
                throw new InvalidOperationException($"{name}: wrote {_output.Position - dataStart} bytes, expected {compressed}.");
        }
        catch
        {
            // Roll the file back to where this entry began; later entries overwrite it.
            _records.RemoveAt(_records.Count - 1);
            _output.Position = offset;
            _output.SetLength(offset);
            throw;
        }
    }

    /// <summary>Writes the central directory and end records. The archive is complete after this.</summary>
    public void Finish()
    {
        var centralStart = _output.Position;
        foreach (var r in _records)
            WriteCentralRecord(r);
        var centralSize = _output.Position - centralStart;

        var needsZip64 = _records.Count >= Max16 || centralStart >= Max32 || centralSize >= Max32;
        if (needsZip64)
        {
            var zip64EndOffset = _output.Position;

            Span<byte> end64 = stackalloc byte[56];
            WriteUInt32(end64, 0, Zip64EndOfCentralDirectorySignature);
            WriteUInt64(end64, 4, 44); // size of the rest of this record
            WriteUInt16(end64, 12, VersionZip64);
            WriteUInt16(end64, 14, VersionZip64);
            WriteUInt32(end64, 16, 0);
            WriteUInt32(end64, 20, 0);
            WriteUInt64(end64, 24, (ulong)_records.Count);
            WriteUInt64(end64, 32, (ulong)_records.Count);
            WriteUInt64(end64, 40, (ulong)centralSize);
            WriteUInt64(end64, 48, (ulong)centralStart);
            _output.Write(end64);

            Span<byte> locator = stackalloc byte[20];
            WriteUInt32(locator, 0, Zip64LocatorSignature);
            WriteUInt32(locator, 4, 0);
            WriteUInt64(locator, 8, (ulong)zip64EndOffset);
            WriteUInt32(locator, 16, 1);
            _output.Write(locator);
        }

        Span<byte> end = stackalloc byte[22];
        WriteUInt32(end, 0, EndOfCentralDirectorySignature);
        WriteUInt16(end, 4, 0);
        WriteUInt16(end, 6, 0);
        var count = (ushort)Math.Min(_records.Count, Max16);
        WriteUInt16(end, 8, count);
        WriteUInt16(end, 10, count);
        WriteUInt32(end, 12, (uint)Math.Min(centralSize, Max32));
        WriteUInt32(end, 16, (uint)Math.Min(centralStart, Max32));
        WriteUInt16(end, 20, 0);
        _output.Write(end);
        _output.Flush();
    }

    private void WriteCentralRecord(CentralRecord r)
    {
        // Only the fields that overflow go into the Zip64 extra, in this fixed order.
        var bigUncompressed = r.UncompressedSize >= Max32;
        var bigCompressed = r.CompressedSize >= Max32;
        var bigOffset = r.LocalHeaderOffset >= Max32;
        var extraLength = (bigUncompressed ? 8 : 0) + (bigCompressed ? 8 : 0) + (bigOffset ? 8 : 0);
        var zip64 = extraLength > 0;

        Span<byte> header = stackalloc byte[46];
        WriteUInt32(header, 0, CentralHeaderSignature);
        WriteUInt16(header, 4, VersionZip64);                // made by: MS-DOS attributes, spec 4.5
        WriteUInt16(header, 6, zip64 ? VersionZip64 : VersionDefault);
        WriteUInt16(header, 8, r.Utf8 ? FlagUtf8 : (ushort)0);
        WriteUInt16(header, 10, r.Method);
        WriteUInt16(header, 12, r.DosTime);
        WriteUInt16(header, 14, r.DosDate);
        WriteUInt32(header, 16, r.Crc);
        WriteUInt32(header, 20, bigCompressed ? Max32 : (uint)r.CompressedSize);
        WriteUInt32(header, 24, bigUncompressed ? Max32 : (uint)r.UncompressedSize);
        WriteUInt16(header, 28, (ushort)r.Name.Length);
        WriteUInt16(header, 30, zip64 ? (ushort)(4 + extraLength) : (ushort)0);
        WriteUInt16(header, 32, 0);                          // comment length
        WriteUInt16(header, 34, 0);                          // disk number
        WriteUInt16(header, 36, 0);                          // internal attributes
        WriteUInt32(header, 38, r.ExternalAttributes);
        WriteUInt32(header, 42, bigOffset ? Max32 : (uint)r.LocalHeaderOffset);
        _output.Write(header);
        _output.Write(r.Name);

        if (zip64)
        {
            Span<byte> extra = stackalloc byte[4 + extraLength];
            WriteUInt16(extra, 0, Zip64ExtraId);
            WriteUInt16(extra, 2, (ushort)extraLength);
            var at = 4;
            if (bigUncompressed) { WriteUInt64(extra, at, (ulong)r.UncompressedSize); at += 8; }
            if (bigCompressed) { WriteUInt64(extra, at, (ulong)r.CompressedSize); at += 8; }
            if (bigOffset) { WriteUInt64(extra, at, (ulong)r.LocalHeaderOffset); }
            _output.Write(extra);
        }
    }

    /// <summary>MS-DOS date/time: local time, 2-second resolution, years 1980–2107.</summary>
    internal static (ushort Time, ushort Date) ToDos(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        if (t.Year > 2107) t = new DateTime(2107, 12, 31, 23, 59, 58);
        var time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
        var date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        return (time, date);
    }

    private static void WriteUInt16(Span<byte> span, int at, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[at..], value);

    private static void WriteUInt32(Span<byte> span, int at, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[at..], value);

    private static void WriteUInt64(Span<byte> span, int at, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(span[at..], value);

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
