using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Grepdesk.Core.SystemInfo;
using Grepdesk.Linux;
using Grepdesk.MacOS;
using Grepdesk.Windows;

namespace Grepdesk.Tests;

public class SystemInfoTests
{
    [Fact]
    public void SmbiosParser_parses_bios_system_board_cpu_cache_and_memory_modules()
    {
        var table = new List<byte>();

        // Type 0: BIOS (length 20)
        AddStruct(table, 0, 20,
            s => { s[0x04] = 1; s[0x05] = 2; s[0x08] = 3; },
            "American Megatrends Inc.", "F.24", "05/12/2025");

        // Type 1: System (length 27)
        AddStruct(table, 1, 27,
            s => { s[0x04] = 1; s[0x05] = 2; },
            "LENOVO", "82WK");

        // Type 2: Baseboard (length 15)
        AddStruct(table, 2, 15,
            s => { s[0x04] = 1; s[0x05] = 2; s[0x06] = 3; },
            "LENOVO", "LNVNB161216", "To Be Filled By O.E.M.");

        // Type 4: Processor (length 48)
        AddStruct(table, 4, 48,
            s =>
            {
                s[0x04] = 1; // Socket
                s[0x07] = 2; // Manufacturer
                s[0x10] = 3; // Version
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x14, 2), 5200); // MaxSpeed
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x16, 2), 2800); // CurrentSpeed
                s[0x23] = 14; // CoreCount
                s[0x25] = 20; // ThreadCount
            },
            "FCBGA1744", "Intel(R) Corporation", "13th Gen Intel(R) Core(TM) i7-13650HX");

        // Type 7: L2 Cache (12 MB) & L3 Cache (24 MB)
        AddStruct(table, 7, 27, s =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x05, 2), 0x0001); // Level 2 (1 + 1)
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x09, 2), 12 * 1024); // 12288 KB = 12 MB
        });
        AddStruct(table, 7, 27, s =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x05, 2), 0x0002); // Level 3 (2 + 1)
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x09, 2), 24 * 1024); // 24576 KB = 24 MB
        });

        // Type 17: Populated SODIMM DDR5 16GB Samsung (0x80CE)
        AddStruct(table, 17, 40,
            s =>
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0C, 2), 16384); // 16384 MB
                s[0x0E] = 0x0D; // SODIMM
                s[0x10] = 1; // DeviceLocator
                s[0x12] = 0x22; // DDR5
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x15, 2), 4800); // Speed
                s[0x17] = 2; // Manufacturer JEDEC ID
                s[0x1A] = 3; // PartNumber
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x20, 2), 4800); // ConfiguredSpeed
            },
            "DIMM 0", "80CE", "M425R2GA3BB0-CQKOD");

        // Type 17: Empty slot
        AddStruct(table, 17, 40,
            s =>
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0C, 2), 0); // Empty
                s[0x10] = 1;
            },
            "DIMM 1");

        // Type 127: End of table
        AddStruct(table, 127, 4, _ => { });

        // Wrap in Windows 8-byte RSMB header
        var raw = new byte[8 + table.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4, 4), (uint)table.Count);
        table.CopyTo(raw, 8);

        var parsed = SmbiosParser.Parse(raw);

        Assert.Equal("American Megatrends Inc.", parsed.BiosVendor);
        Assert.Equal("F.24", parsed.BiosVersion);
        Assert.Equal("05/12/2025", parsed.BiosReleaseDate);

        Assert.Equal("LENOVO", parsed.SystemManufacturer);
        Assert.Equal("82WK", parsed.SystemProductName);

        Assert.Equal("LENOVO", parsed.BaseboardManufacturer);
        Assert.Equal("LNVNB161216", parsed.BaseboardProduct);
        Assert.Null(parsed.BaseboardVersion); // "To Be Filled By O.E.M." filtered

        Assert.Equal("FCBGA1744", parsed.ProcessorSocket);
        Assert.Equal(5200, parsed.ProcessorMaxSpeedMhz);
        Assert.Equal(2800, parsed.ProcessorCurrentSpeedMhz);
        Assert.Equal(14, parsed.ProcessorCoreCount);
        Assert.Equal(20, parsed.ProcessorThreadCount);

        Assert.Equal(12L * 1024 * 1024, parsed.L2CacheBytes);
        Assert.Equal(24L * 1024 * 1024, parsed.L3CacheBytes);

        Assert.Equal(2, parsed.TotalMemorySlots);
        Assert.Equal(1, parsed.UsedMemorySlots);
        var mod = Assert.Single(parsed.MemoryModules);
        Assert.Equal("DIMM 0", mod.Slot);
        Assert.Equal(16L * 1024 * 1024 * 1024, mod.CapacityBytes);
        Assert.Equal("DDR5", mod.MemoryType);
        Assert.Equal(4800, mod.SpeedMhz);
        Assert.Equal("Samsung", mod.Manufacturer);
        Assert.Equal("M425R2GA3BB0-CQKOD", mod.PartNumber);
        Assert.Equal("SODIMM", mod.FormFactor);
    }

    [Fact]
    public void SmbiosParser_handles_empty_or_truncated_buffers_safely()
    {
        Assert.Empty(SmbiosParser.Parse([]).MemoryModules);
        Assert.Empty(SmbiosParser.Parse([1, 2, 3]).MemoryModules);
        Assert.Empty(SmbiosParser.ParseTable([0x11, 0x05, 0x00]).MemoryModules);
    }

    [Fact]
    public void SystemInfoFormatter_formats_sizes_clocks_uptime_and_summary()
    {
        Assert.Equal("16 GB", SystemInfoFormatter.FormatBinarySize(16L * 1024 * 1024 * 1024));
        Assert.Equal("512 GB", SystemInfoFormatter.FormatStorageCapacity(512_000_000_000L));
        Assert.Equal("1 TB", SystemInfoFormatter.FormatStorageCapacity(1_000_000_000_000L));
        Assert.Equal("2.80 GHz", SystemInfoFormatter.FormatCpuClock(2800));
        Assert.Equal("4800 MT/s", SystemInfoFormatter.FormatMemorySpeed(4800, "DDR5"));
        Assert.Equal("2d 4h 15m", SystemInfoFormatter.FormatUptime(new TimeSpan(2, 4, 15, 0)));

        var snap = CreateSampleSnapshot();
        var compact = SystemInfoFormatter.FormatPlainText(snap, detailed: false);
        Assert.Contains("CPU:         Intel Core i7-13650HX", compact);
        Assert.Contains("GPU:         NVIDIA GeForce RTX 4060 (8 GB)", compact);
        Assert.Contains("RAM:         16 GB DDR5 @ 4800 MT/s", compact);
        Assert.Contains("Storage:     Samsung 990 PRO (1 TB, NVMe SSD) [C:]", compact);

        var detailed = SystemInfoFormatter.FormatPlainText(snap, detailed: true);
        Assert.Contains("L3 Cache:  24 MB", detailed);
        Assert.Contains("Driver:    32.0.16.2124", detailed);
        Assert.Contains("BIOS:      Insyde F.24", detailed);
    }

    [Fact]
    public void Windows_product_name_normalizes_win10_to_win11_on_build_22000_plus()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal("Windows 11 Home", WindowsSystemInfoProvider.NormalizeWindowsProductName("Windows 10 Home", "26300"));
        Assert.Equal("Windows 11 Pro", WindowsSystemInfoProvider.NormalizeWindowsProductName("Windows 10 Pro", "22000"));
        Assert.Equal("Windows 10 Pro", WindowsSystemInfoProvider.NormalizeWindowsProductName("Windows 10 Pro", "19045"));
    }

    [Fact]
    public void WindowsSystemInfoProvider_live_smoke_test()
    {
        if (!OperatingSystem.IsWindows()) return;

        var provider = new WindowsSystemInfoProvider();
        var info = provider.GetSystemInfo();

        Assert.False(string.IsNullOrWhiteSpace(info.Os.OsName));
        Assert.False(string.IsNullOrWhiteSpace(info.Cpu.Name));
        Assert.True(info.Cpu.PhysicalCores > 0);
        Assert.True(info.Cpu.LogicalProcessors >= info.Cpu.PhysicalCores);
        Assert.True(info.Memory.TotalUsableBytes > 0);
        Assert.NotEmpty(info.StorageDrives);
    }

    [Fact]
    public void Linux_and_mac_parsers_extract_expected_fields()
    {
        var (osName, osVer) = LinuxSystemInfoProvider.ParseOsRelease("PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nVERSION_ID=\"24.04\"\n");
        Assert.Equal("Ubuntu 24.04.1 LTS", osName);
        Assert.Equal("24.04", osVer);

        var cpu = LinuxSystemInfoProvider.ParseCpuInfo(
            "processor\t: 0\nvendor_id\t: AuthenticAMD\nmodel name\t: AMD Ryzen 7 7840U\ncpu MHz\t\t: 3300.00\ncpu cores\t: 8\nprocessor\t: 1\n",
            "x64", 2);
        Assert.Equal("AMD Ryzen 7 7840U", cpu.Name);
        Assert.Equal("AMD", cpu.Manufacturer);
        Assert.Equal(8, cpu.PhysicalCores);
        Assert.Equal(2, cpu.LogicalProcessors);
        Assert.Equal(3300, cpu.BaseClockMhz);

        Assert.Equal(16384L * 1024, LinuxSystemInfoProvider.ParseMemInfo("MemTotal:       16384 kB\n"));

        var gpus = LinuxSystemInfoProvider.ParseLspciGpus("01:00.0 \"VGA compatible controller\" \"NVIDIA Corporation\" \"AD107M [GeForce RTX 4060 Max-Q / Mobile]\"");
        var gpu = Assert.Single(gpus);
        Assert.Contains("RTX 4060", gpu.Name);

        var macGpus = MacSystemInfoProvider.ParseSystemProfilerDisplays("""{"SPDisplaysDataType":[{"sppci_model":"Apple M3 Pro","spdisplays_vendor":"sppci_vendor_Apple"}]}""");
        Assert.Equal("Apple M3 Pro", Assert.Single(macGpus).Name);

        var macDrives = MacSystemInfoProvider.ParseSystemProfilerStorage("""{"SPStorageDataType":[{"_name":"Macintosh HD","size_in_bytes":500000000000,"mount_point":"/"}]}""");
        Assert.Equal("Macintosh HD", Assert.Single(macDrives).Model);
    }

    private static void AddStruct(List<byte> target, byte type, byte length, Action<byte[]> populate, params string[] strings)
    {
        var buf = new byte[length];
        buf[0] = type;
        buf[1] = length;
        populate(buf);
        target.AddRange(buf);

        if (strings.Length == 0)
        {
            target.Add(0);
            target.Add(0);
        }
        else
        {
            foreach (var s in strings)
            {
                target.AddRange(Encoding.ASCII.GetBytes(s));
                target.Add(0);
            }
            target.Add(0);
        }
    }

    internal static SystemInfoSnapshot CreateSampleSnapshot() => new(
        CollectedAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Os: new OsSystemInfo("Windows 11 Home", "26H2 (Build 26300.9974)", "x64", "MY-PC", TimeSpan.FromHours(5)),
        Cpu: new CpuInfo("Intel Core i7-13650HX", "Intel", "x64", 14, 20, 2800, 4900, "FCBGA1744", 12L * 1024 * 1024, 24L * 1024 * 1024),
        Motherboard: new MotherboardInfo("LENOVO", "LNVNB161216", null, "LENOVO", "82WK", "Insyde", "F.24", "05/12/2025"),
        Memory: new MemoryInfo(
            16L * 1024 * 1024 * 1024,
            16L * 1024 * 1024 * 1024,
            "DDR5",
            4800,
            2,
            1,
            [new MemoryModuleInfo("DIMM 0", 16L * 1024 * 1024 * 1024, "DDR5", 4800, "Samsung", "M425", "SODIMM")]),
        Gpus: [new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L * 1024 * 1024 * 1024, null, "32.0.16.2124", "11-7-2026")],
        StorageDrives: [new StorageDriveInfo("Samsung 990 PRO", 1_000_000_000_000L, "NVMe SSD", "NVMe", "4B2QJXD7", ["C:"])],
        Displays: [new DisplayInfo("Built-in Display", 1920, 1200, 165, true, "eDP (Internal)")]);
}
