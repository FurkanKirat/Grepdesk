using System.Buffers.Binary;
using System.Text;

namespace Grepdesk.Core.Viewer;

/// <summary>
/// Minimal read-only Microsoft Compound File Binary Format (OLE2 / MS-CFB)
/// container reader for legacy binary Office files (.ppt, .doc, .xls).
/// </summary>
internal sealed class CompoundFile : IDisposable
{
    private const ulong Signature = 0xE11AB1A1E011CFD0UL; // D0 CF 11 E0 A1 B1 1A E1 little-endian
    private const uint FreeSect = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSect = 0xFFFFFFFD;
    private const uint DifSect = 0xFFFFFFFC;
    private const uint MaxValidSector = 0xFFFFFFFB;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly int _majorVersion;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly Dictionary<string, DirectoryEntry> _rootStreams;

    private sealed record DirectoryEntry(string Name, byte ObjectType, uint LeftSibling, uint RightSibling, uint Child, uint StartSector, long Size);

    private CompoundFile(
        Stream stream,
        bool ownsStream,
        int majorVersion,
        int sectorSize,
        int miniSectorSize,
        uint miniStreamCutoff,
        uint[] fat,
        uint[] miniFat,
        byte[] miniStream,
        Dictionary<string, DirectoryEntry> rootStreams)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _majorVersion = majorVersion;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniStreamCutoff = miniStreamCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _miniStream = miniStream;
        _rootStreams = rootStreams;
    }

    public static bool HasSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && BinaryPrimitives.ReadUInt64LittleEndian(header) == Signature;

    public static CompoundFile Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(fs, ownsStream: true);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static CompoundFile Open(Stream stream, bool ownsStream = false)
    {
        if (!stream.CanSeek)
            throw new InvalidDataException("Compound file stream must be seekable.");
        if (stream.Length < 512)
            throw new InvalidDataException("File is too small to be a valid OLE2 compound file.");

        var header = new byte[512];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header);

        if (!HasSignature(header))
            throw new InvalidDataException("Invalid OLE2 compound file signature.");

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26, 2));
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
        if (byteOrder != 0xFFFE)
            throw new InvalidDataException("Unsupported compound file byte order.");

        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
        if (sectorShift is < 7 or > 16)
            throw new InvalidDataException($"Invalid compound file sector shift: {sectorShift}.");
        if (miniSectorShift is < 4 || miniSectorShift > sectorShift)
            throw new InvalidDataException($"Invalid compound file mini-sector shift: {miniSectorShift}.");

        var sectorSize = 1 << sectorShift;
        var miniSectorSize = 1 << miniSectorShift;
        var csectFat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44, 4));
        var sectDirStart = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48, 4));
        var miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(56, 4));
        var sectMiniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(60, 4));
        var csectMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64, 4));
        var sectDifStart = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68, 4));
        var csectDif = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(72, 4));

        var maxSectors = (int)Math.Min(int.MaxValue, Math.Max(1, (stream.Length / sectorSize) + 4));
        var fatSectors = CollectFatSectors(stream, header, sectorSize, sectDifStart, csectDif, csectFat, maxSectors);
        var fat = ReadFat(stream, fatSectors, sectorSize);

        var dirBytes = ReadChain(stream, fat, sectorSize, sectDirStart, expectedLength: -1, maxSectors);
        var entries = ParseDirectory(dirBytes, majorVersion);
        if (entries.Count == 0 || entries[0].ObjectType != 5)
            throw new InvalidDataException("Compound file is missing a root storage directory entry.");

        var root = entries[0];
        var miniStream = root.StartSector <= MaxValidSector && root.Size > 0
            ? ReadChain(stream, fat, sectorSize, root.StartSector, root.Size, maxSectors)
            : [];

        uint[] miniFat = [];
        if (csectMiniFat > 0 && sectMiniFatStart <= MaxValidSector)
        {
            var miniFatBytes = ReadChain(stream, fat, sectorSize, sectMiniFatStart, expectedLength: -1, maxSectors);
            miniFat = new uint[miniFatBytes.Length / 4];
            for (var i = 0; i < miniFat.Length; i++)
                miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4, 4));
        }

        var rootStreams = CollectRootChildren(entries, root.Child);
        return new CompoundFile(stream, ownsStream, majorVersion, sectorSize, miniSectorSize, miniCutoff, fat, miniFat, miniStream, rootStreams);
    }

    public bool TryReadStream(string name, out byte[] data)
    {
        if (!_rootStreams.TryGetValue(name, out var entry) || entry.ObjectType != 2)
        {
            data = [];
            return false;
        }

        if (entry.Size <= 0 || entry.StartSector > MaxValidSector)
        {
            data = [];
            return true;
        }

        if (entry.Size < _miniStreamCutoff)
        {
            data = ReadMiniChain(_miniStream, _miniFat, _miniSectorSize, entry.StartSector, entry.Size);
            return true;
        }

        var maxSectors = (int)Math.Min(int.MaxValue, Math.Max(1, (_stream.Length / _sectorSize) + 4));
        data = ReadChain(_stream, _fat, _sectorSize, entry.StartSector, entry.Size, maxSectors);
        return true;
    }

    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }

    private static List<uint> CollectFatSectors(
        Stream stream,
        byte[] header,
        int sectorSize,
        uint sectDifStart,
        uint csectDif,
        uint csectFat,
        int maxSectors)
    {
        var fatSectors = new List<uint>((int)Math.Min(csectFat, 4096));
        for (var i = 0; i < 109; i++)
        {
            var s = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76 + i * 4, 4));
            if (s <= MaxValidSector)
                fatSectors.Add(s);
        }

        var intsPerSector = sectorSize / 4;
        var visited = new HashSet<uint>();
        var difSect = sectDifStart;
        var sectorBuf = new byte[sectorSize];
        var remainingDif = csectDif;

        while (remainingDif > 0 && difSect <= MaxValidSector && visited.Add(difSect) && visited.Count <= maxSectors)
        {
            if (!TryReadSector(stream, difSect, sectorSize, sectorBuf))
                break;

            for (var i = 0; i < intsPerSector - 1; i++)
            {
                var s = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(i * 4, 4));
                if (s <= MaxValidSector)
                    fatSectors.Add(s);
            }

            difSect = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan((intsPerSector - 1) * 4, 4));
            remainingDif--;
        }

        if (csectFat > 0 && fatSectors.Count > csectFat)
            fatSectors.RemoveRange((int)csectFat, fatSectors.Count - (int)csectFat);

        return fatSectors;
    }

    private static uint[] ReadFat(Stream stream, List<uint> fatSectors, int sectorSize)
    {
        var intsPerSector = sectorSize / 4;
        var fat = new uint[fatSectors.Count * intsPerSector];
        var sectorBuf = new byte[sectorSize];

        for (var i = 0; i < fatSectors.Count; i++)
        {
            if (!TryReadSector(stream, fatSectors[i], sectorSize, sectorBuf))
                continue;

            var baseIdx = i * intsPerSector;
            for (var j = 0; j < intsPerSector; j++)
                fat[baseIdx + j] = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(j * 4, 4));
        }

        return fat;
    }

    private static byte[] ReadChain(Stream stream, uint[] fat, int sectorSize, uint startSector, long expectedLength, int maxSectors)
    {
        if (startSector > MaxValidSector)
            return [];

        using var ms = expectedLength is > 0 and <= 64 * 1024 * 1024
            ? new MemoryStream((int)expectedLength)
            : new MemoryStream();

        var sectorBuf = new byte[sectorSize];
        var visited = new HashSet<uint>();
        var current = startSector;

        while (current <= MaxValidSector && current < fat.Length && visited.Add(current) && visited.Count <= maxSectors)
        {
            if (!TryReadSector(stream, current, sectorSize, sectorBuf))
                break;

            if (expectedLength >= 0)
            {
                var remaining = expectedLength - ms.Length;
                if (remaining <= 0) break;
                ms.Write(sectorBuf, 0, (int)Math.Min(sectorSize, remaining));
            }
            else
            {
                ms.Write(sectorBuf, 0, sectorSize);
            }

            current = fat[current];
        }

        return ms.ToArray();
    }

    private static byte[] ReadMiniChain(byte[] miniStream, uint[] miniFat, int miniSectorSize, uint startMiniSector, long expectedLength)
    {
        if (startMiniSector > MaxValidSector || miniStream.Length == 0)
            return [];

        var maxMiniSectors = Math.Max(1, (miniStream.Length / miniSectorSize) + 4);
        using var ms = expectedLength > 0 ? new MemoryStream((int)Math.Min(expectedLength, miniStream.Length)) : new MemoryStream();
        var visited = new HashSet<uint>();
        var current = startMiniSector;

        while (current <= MaxValidSector && current < miniFat.Length && visited.Add(current) && visited.Count <= maxMiniSectors)
        {
            var offset = (long)current * miniSectorSize;
            if (offset < 0 || offset >= miniStream.Length)
                break;

            var available = (int)Math.Min(miniSectorSize, miniStream.Length - offset);
            if (expectedLength >= 0)
            {
                var remaining = expectedLength - ms.Length;
                if (remaining <= 0) break;
                available = (int)Math.Min(available, remaining);
            }

            ms.Write(miniStream, (int)offset, available);
            if (expectedLength >= 0 && ms.Length >= expectedLength)
                break;

            current = miniFat[current];
        }

        return ms.ToArray();
    }

    private static bool TryReadSector(Stream stream, uint sectorId, int sectorSize, byte[] buffer)
    {
        var offset = ((long)sectorId + 1L) * sectorSize;
        if (offset < 0 || offset >= stream.Length)
            return false;

        stream.Seek(offset, SeekOrigin.Begin);
        var total = 0;
        while (total < sectorSize)
        {
            var read = stream.Read(buffer, total, sectorSize - total);
            if (read == 0)
            {
                Array.Clear(buffer, total, sectorSize - total);
                return total > 0;
            }
            total += read;
        }
        return true;
    }

    private static List<DirectoryEntry> ParseDirectory(byte[] dirBytes, int majorVersion)
    {
        var count = dirBytes.Length / 128;
        var entries = new List<DirectoryEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var span = dirBytes.AsSpan(i * 128, 128);
            var cb = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(64, 2));
            var charBytes = Math.Clamp(cb - 2, 0, 62);
            var name = charBytes > 0 ? Encoding.Unicode.GetString(span.Slice(0, charBytes)) : "";
            var objectType = span[66];
            var left = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(68, 4));
            var right = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(72, 4));
            var child = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(76, 4));
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(116, 4));
            var rawSize = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(120, 8));
            var size = majorVersion == 3 ? (long)(rawSize & 0xFFFFFFFFUL) : (long)Math.Min(rawSize, long.MaxValue);

            entries.Add(new DirectoryEntry(name, objectType, left, right, child, startSector, size));
        }

        return entries;
    }

    private static Dictionary<string, DirectoryEntry> CollectRootChildren(List<DirectoryEntry> entries, uint rootChildId)
    {
        var map = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        if (rootChildId >= entries.Count)
            return map;

        var visited = new HashSet<uint>();
        var stack = new Stack<uint>();
        stack.Push(rootChildId);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id >= entries.Count || !visited.Add(id))
                continue;

            var entry = entries[(int)id];
            if (entry.ObjectType == 2 && entry.Name.Length > 0)
                map.TryAdd(entry.Name, entry);

            if (entry.LeftSibling < entries.Count) stack.Push(entry.LeftSibling);
            if (entry.RightSibling < entries.Count) stack.Push(entry.RightSibling);
        }

        return map;
    }
}
