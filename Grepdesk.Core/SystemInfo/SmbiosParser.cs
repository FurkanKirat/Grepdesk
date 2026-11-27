using System.Buffers.Binary;
using System.Text;

namespace Grepdesk.Core.SystemInfo;

public sealed class SmbiosData
{
    public string? BiosVendor { get; init; }
    public string? BiosVersion { get; init; }
    public string? BiosReleaseDate { get; init; }

    public string? SystemManufacturer { get; init; }
    public string? SystemProductName { get; init; }

    public string? BaseboardManufacturer { get; init; }
    public string? BaseboardProduct { get; init; }
    public string? BaseboardVersion { get; init; }

    public string? ProcessorSocket { get; init; }
    public string? ProcessorManufacturer { get; init; }
    public string? ProcessorVersion { get; init; }
    public int? ProcessorMaxSpeedMhz { get; init; }
    public int? ProcessorCurrentSpeedMhz { get; init; }
    public int? ProcessorCoreCount { get; init; }
    public int? ProcessorThreadCount { get; init; }

    public long? L2CacheBytes { get; init; }
    public long? L3CacheBytes { get; init; }

    public int TotalMemorySlots { get; init; }
    public int UsedMemorySlots { get; init; }
    public IReadOnlyList<MemoryModuleInfo> MemoryModules { get; init; } = [];
}

/// <summary>
/// Pure binary parser for SMBIOS / DMI firmware tables (Type 0, 1, 2, 4, 7, 17).
/// </summary>
public static class SmbiosParser
{
    private static readonly HashSet<string> JunkStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.",
        "Default string",
        "Not Specified",
        "None",
        "Unknown",
        "Undefined",
        "O.E.M.",
        "0000",
        "N/A",
        "NO DIMM",
        "Empty",
        "System Product Name",
        "System Manufacturer",
        "System Version",
        "Base Board Product Name",
        "Not Available"
    };

    private static readonly Dictionary<string, string> JedecManufacturers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0198"] = "Kingston",
        ["9801"] = "Kingston",
        ["029E"] = "Corsair",
        ["9E02"] = "Corsair",
        ["04CD"] = "G.Skill",
        ["CD04"] = "G.Skill",
        ["80CE"] = "Samsung",
        ["00CE"] = "Samsung",
        ["CE00"] = "Samsung",
        ["80AD"] = "SK Hynix",
        ["00AD"] = "SK Hynix",
        ["AD00"] = "SK Hynix",
        ["802C"] = "Micron",
        ["002C"] = "Micron",
        ["2C00"] = "Micron",
        ["059B"] = "Crucial",
        ["859B"] = "Crucial",
        ["9B05"] = "Crucial",
        ["04CB"] = "ADATA",
        ["CB04"] = "ADATA",
        ["02FE"] = "Elpida",
        ["FE02"] = "Elpida",
        ["830B"] = "Nanya",
        ["030B"] = "Nanya",
        ["014F"] = "Transcend",
        ["04EF"] = "Team Group",
        ["8551"] = "Qimonda",
        ["0215"] = "Patriot",
        ["869D"] = "PNY"
    };

    /// <summary>
    /// Parses a Windows <c>GetSystemFirmwareTable('RSMB', ...)</c> buffer (8-byte header + table).
    /// </summary>
    public static SmbiosData Parse(ReadOnlySpan<byte> rawSmbiosData)
    {
        if (rawSmbiosData.Length < 8)
            return new SmbiosData();

        var tableLength = BinaryPrimitives.ReadUInt32LittleEndian(rawSmbiosData.Slice(4, 4));
        var available = rawSmbiosData.Length - 8;
        var len = (int)Math.Min(tableLength, (uint)available);
        return ParseTable(rawSmbiosData.Slice(8, len));
    }

    /// <summary>
    /// Parses raw SMBIOS structure table bytes directly (e.g. from Linux sysfs or unit tests).
    /// </summary>
    public static SmbiosData ParseTable(ReadOnlySpan<byte> tableBytes)
    {
        string? biosVendor = null, biosVersion = null, biosReleaseDate = null;
        string? sysManufacturer = null, sysProduct = null;
        string? boardManufacturer = null, boardProduct = null, boardVersion = null;
        string? cpuSocket = null, cpuManufacturer = null, cpuVersion = null;
        int? cpuMaxSpeed = null, cpuCurrentSpeed = null, cpuCores = null, cpuThreads = null;
        long l2CacheTotal = 0, l3CacheTotal = 0;
        int totalSlots = 0, usedSlots = 0;
        var modules = new List<MemoryModuleInfo>();

        var offset = 0;
        while (offset + 4 <= tableBytes.Length)
        {
            var type = tableBytes[offset];
            var length = tableBytes[offset + 1];
            if (length < 4 || offset + length > tableBytes.Length)
                break;

            var formatted = tableBytes.Slice(offset, length);

            // Locate the end of the string-set (double null terminator: 0x00 0x00)
            var stringsStart = offset + length;
            var pos = stringsStart;
            while (pos + 1 < tableBytes.Length)
            {
                if (tableBytes[pos] == 0 && tableBytes[pos + 1] == 0)
                    break;
                pos++;
            }

            if (pos + 1 >= tableBytes.Length)
                break;

            var stringsSpan = tableBytes.Slice(stringsStart, pos - stringsStart);
            var nextOffset = pos + 2;

            switch (type)
            {
                case 0: // BIOS Information
                    biosVendor ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x04)));
                    biosVersion ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x05)));
                    biosReleaseDate ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x08)));
                    break;

                case 1: // System Information
                    sysManufacturer ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x04)));
                    sysProduct ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x05)));
                    break;

                case 2: // Baseboard Information
                    boardManufacturer ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x04)));
                    boardProduct ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x05)));
                    boardVersion ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x06)));
                    break;

                case 4: // Processor Information
                    cpuSocket ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x04)));
                    cpuManufacturer ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x07)));
                    cpuVersion ??= CleanString(GetString(stringsSpan, ReadByte(formatted, 0x10)));

                    var maxSpd = ReadUInt16(formatted, 0x14);
                    if (maxSpd > 0) cpuMaxSpeed ??= maxSpd;

                    var curSpd = ReadUInt16(formatted, 0x16);
                    if (curSpd > 0) cpuCurrentSpeed ??= curSpd;

                    var cores8 = ReadByte(formatted, 0x23);
                    if (cores8 == 0xFF)
                    {
                        var cores16 = ReadUInt16(formatted, 0x2A);
                        if (cores16 > 0) cpuCores ??= cores16;
                    }
                    else if (cores8 > 0)
                    {
                        cpuCores ??= cores8;
                    }

                    var threads8 = ReadByte(formatted, 0x25);
                    if (threads8 == 0xFF)
                    {
                        var threads16 = ReadUInt16(formatted, 0x2E);
                        if (threads16 > 0) cpuThreads ??= threads16;
                    }
                    else if (threads8 > 0)
                    {
                        cpuThreads ??= threads8;
                    }
                    break;

                case 7: // Cache Information
                {
                    var config = ReadUInt16(formatted, 0x05);
                    var level = (config & 0x07) + 1;
                    long cacheBytes = 0;

                    var installedSize = ReadUInt16(formatted, 0x09);
                    if (installedSize == 0xFFFF && formatted.Length >= 0x1B)
                    {
                        var extSize = ReadUInt32(formatted, 0x17);
                        if (extSize != 0)
                        {
                            var is64K = (extSize & 0x80000000) != 0;
                            var units = extSize & 0x7FFFFFFF;
                            cacheBytes = units * (is64K ? 65536L : 1024L);
                        }
                    }
                    else if (installedSize > 0)
                    {
                        var is64K = (installedSize & 0x8000) != 0;
                        var units = installedSize & 0x7FFF;
                        cacheBytes = units * (is64K ? 65536L : 1024L);
                    }

                    if (level == 2) l2CacheTotal += cacheBytes;
                    else if (level == 3) l3CacheTotal += cacheBytes;
                    break;
                }

                case 17: // Memory Device
                {
                    totalSlots++;
                    var sizeField = ReadUInt16(formatted, 0x0C);
                    long capacityBytes = 0;

                    if (sizeField == 0x7FFF && formatted.Length >= 0x20)
                    {
                        var extMb = ReadUInt32(formatted, 0x1C) & 0x7FFFFFFF;
                        capacityBytes = extMb * 1024L * 1024L;
                    }
                    else if (sizeField != 0 && sizeField != 0xFFFF)
                    {
                        var isKb = (sizeField & 0x8000) != 0;
                        var units = sizeField & 0x7FFF;
                        capacityBytes = units * (isKb ? 1024L : 1024L * 1024L);
                    }

                    if (capacityBytes > 0)
                    {
                        usedSlots++;
                        var formFactor = DecodeFormFactor(ReadByte(formatted, 0x0E));
                        var deviceLocator = CleanString(GetString(stringsSpan, ReadByte(formatted, 0x10)));
                        var bankLocator = CleanString(GetString(stringsSpan, ReadByte(formatted, 0x11)));
                        var slot = deviceLocator ?? bankLocator;

                        var memType = DecodeMemoryType(ReadByte(formatted, 0x12));

                        int? speed = null;
                        var spd16 = ReadUInt16(formatted, 0x15);
                        if (spd16 == 0xFFFF && formatted.Length >= 0x58)
                        {
                            var extSpd = ReadUInt32(formatted, 0x54);
                            if (extSpd > 0) speed = (int)extSpd;
                        }
                        else if (spd16 > 0)
                        {
                            speed = spd16;
                        }

                        var cfgSpd16 = ReadUInt16(formatted, 0x20);
                        if (cfgSpd16 == 0xFFFF && formatted.Length >= 0x5C)
                        {
                            var extCfg = ReadUInt32(formatted, 0x58);
                            if (extCfg > 0) speed = (int)extCfg;
                        }
                        else if (cfgSpd16 > 0)
                        {
                            speed = cfgSpd16;
                        }

                        var rawMfg = CleanString(GetString(stringsSpan, ReadByte(formatted, 0x17)));
                        var manufacturer = DecodeManufacturer(rawMfg);
                        var partNumber = CleanString(GetString(stringsSpan, ReadByte(formatted, 0x1A)));

                        modules.Add(new MemoryModuleInfo(
                            Slot: slot,
                            CapacityBytes: capacityBytes,
                            MemoryType: memType,
                            SpeedMhz: speed,
                            Manufacturer: manufacturer,
                            PartNumber: partNumber,
                            FormFactor: formFactor));
                    }
                    break;
                }

                case 127: // End-of-table
                    offset = tableBytes.Length;
                    continue;
            }

            offset = nextOffset;
        }

        return new SmbiosData
        {
            BiosVendor = biosVendor,
            BiosVersion = biosVersion,
            BiosReleaseDate = biosReleaseDate,
            SystemManufacturer = sysManufacturer,
            SystemProductName = sysProduct,
            BaseboardManufacturer = boardManufacturer,
            BaseboardProduct = boardProduct,
            BaseboardVersion = boardVersion,
            ProcessorSocket = cpuSocket,
            ProcessorManufacturer = cpuManufacturer,
            ProcessorVersion = cpuVersion,
            ProcessorMaxSpeedMhz = cpuMaxSpeed,
            ProcessorCurrentSpeedMhz = cpuCurrentSpeed,
            ProcessorCoreCount = cpuCores,
            ProcessorThreadCount = cpuThreads,
            L2CacheBytes = l2CacheTotal > 0 ? l2CacheTotal : null,
            L3CacheBytes = l3CacheTotal > 0 ? l3CacheTotal : null,
            TotalMemorySlots = totalSlots,
            UsedMemorySlots = usedSlots,
            MemoryModules = modules
        };
    }

    public static string? CleanString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = CollapseSpaces(value.Trim());
        if (trimmed.Length == 0 || JunkStrings.Contains(trimmed))
            return null;

        return trimmed;
    }

    public static string CollapseSpaces(string input)
    {
        if (!input.Contains("  ", StringComparison.Ordinal))
            return input;

        var sb = new StringBuilder(input.Length);
        var prevSpace = false;
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    private static string? DecodeManufacturer(string? raw)
    {
        if (raw is null)
            return null;

        var stripped = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        if (JedecManufacturers.TryGetValue(stripped, out var mapped))
            return mapped;

        return raw;
    }

    private static string? DecodeMemoryType(byte code) => code switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "LPDDR5X",
        _ => null
    };

    private static string? DecodeFormFactor(byte code) => code switch
    {
        0x09 => "DIMM",
        0x0D => "SODIMM",
        0x0F => "Onboard",
        0x0B => "RIMM",
        _ => null
    };

    private static byte ReadByte(ReadOnlySpan<byte> span, int index) =>
        index < span.Length ? span[index] : (byte)0;

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, int index) =>
        index + 2 <= span.Length ? BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(index, 2)) : (ushort)0;

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int index) =>
        index + 4 <= span.Length ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(index, 4)) : 0u;

    private static string? GetString(ReadOnlySpan<byte> stringsSpan, byte oneBasedIndex)
    {
        if (oneBasedIndex == 0 || stringsSpan.IsEmpty)
            return null;

        var current = 1;
        var start = 0;
        for (var i = 0; i <= stringsSpan.Length; i++)
        {
            if (i == stringsSpan.Length || stringsSpan[i] == 0)
            {
                if (current == oneBasedIndex)
                {
                    if (i <= start)
                        return null;
                    return Encoding.ASCII.GetString(stringsSpan.Slice(start, i - start));
                }
                current++;
                start = i + 1;
            }
        }
        return null;
    }
}
