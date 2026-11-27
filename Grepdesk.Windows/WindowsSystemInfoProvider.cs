using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Grepdesk.Core.SystemInfo;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Grepdesk.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    public SystemInfoSnapshot GetSystemInfo()
    {
        var smbios = ReadSmbios();
        var os = ReadOsInfo();
        var cpu = ReadCpuInfo(smbios);
        var motherboard = ReadMotherboardInfo(smbios);
        var memory = ReadMemoryInfo(smbios);
        var gpus = ReadGpuInfo();
        var storage = ReadStorageInfo();
        var displays = ReadDisplayInfo();

        return new SystemInfoSnapshot(
            CollectedAtUtc: DateTime.UtcNow,
            Os: os,
            Cpu: cpu,
            Motherboard: motherboard,
            Memory: memory,
            Gpus: gpus,
            StorageDrives: storage,
            Displays: displays);
    }

    // =========================================================================
    // SMBIOS
    // =========================================================================

    private static SmbiosData ReadSmbios()
    {
        try
        {
            const uint rsmb = 0x52534D42; // 'RSMB'
            var size = GetSystemFirmwareTable(rsmb, 0, IntPtr.Zero, 0);
            if (size == 0 || size > 16 * 1024 * 1024)
                return new SmbiosData();

            var buffer = new byte[size];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var read = GetSystemFirmwareTable(rsmb, 0, handle.AddrOfPinnedObject(), size);
                if (read == 0)
                    return new SmbiosData();
                return SmbiosParser.Parse(buffer.AsSpan(0, (int)read));
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return new SmbiosData();
        }
    }

    // =========================================================================
    // OS & System
    // =========================================================================

    private static OsSystemInfo ReadOsInfo()
    {
        var arch = SystemInfoFormatter.FormatArchitecture(RuntimeInformation.OSArchitecture);
        var uptime = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64));
        string osName = RuntimeInformation.OSDescription.Trim();
        string? osVersion = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                var productName = key.GetValue("ProductName") as string;
                var displayVersion = key.GetValue("DisplayVersion") as string;
                var currentBuild = key.GetValue("CurrentBuild") as string;
                var ubr = key.GetValue("UBR");

                var normalized = NormalizeWindowsProductName(productName, currentBuild);
                if (!string.IsNullOrWhiteSpace(normalized))
                    osName = normalized;

                var buildPart = !string.IsNullOrWhiteSpace(currentBuild)
                    ? (ubr is not null ? $"Build {currentBuild}.{ubr}" : $"Build {currentBuild}")
                    : null;

                osVersion = (!string.IsNullOrWhiteSpace(displayVersion), buildPart is not null) switch
                {
                    (true, true) => $"{displayVersion} ({buildPart})",
                    (true, false) => displayVersion,
                    (false, true) => buildPart,
                    _ => null
                };
            }
        }
        catch
        {
            // Fall back to RuntimeInformation.OSDescription.
        }

        return new OsSystemInfo(
            OsName: osName,
            OsVersion: osVersion,
            Architecture: arch,
            MachineName: Environment.MachineName,
            Uptime: uptime);
    }

    public static string NormalizeWindowsProductName(string? productName, string? currentBuild)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return "Windows";

        if (int.TryParse(currentBuild, out var build) && build >= 22000 &&
            productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            return productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        return productName;
    }

    // =========================================================================
    // CPU
    // =========================================================================

    private static CpuInfo ReadCpuInfo(SmbiosData smbios)
    {
        string? name = null;
        string? vendor = null;
        int? baseMhz = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is not null)
            {
                name = SmbiosParser.CleanString(key.GetValue("ProcessorNameString") as string);
                vendor = SmbiosParser.CleanString(key.GetValue("VendorIdentifier") as string);
                if (key.GetValue("~MHz") is int mhz && mhz > 0)
                    baseMhz = mhz;
            }
        }
        catch
        {
            // Fall back to SMBIOS.
        }

        name ??= smbios.ProcessorVersion ?? $"{RuntimeInformation.ProcessArchitecture} Processor";
        vendor ??= smbios.ProcessorManufacturer;
        baseMhz ??= smbios.ProcessorCurrentSpeedMhz;

        var (physicalCores, l2Bytes, l3Bytes) = ReadLogicalProcessorDetails();
        if (physicalCores <= 0)
            physicalCores = smbios.ProcessorCoreCount ?? Environment.ProcessorCount;
        if (l2Bytes <= 0 && smbios.L2CacheBytes is > 0)
            l2Bytes = smbios.L2CacheBytes.Value;
        if (l3Bytes <= 0 && smbios.L3CacheBytes is > 0)
            l3Bytes = smbios.L3CacheBytes.Value;

        return new CpuInfo(
            Name: name,
            Manufacturer: MapCpuVendor(vendor),
            Architecture: SystemInfoFormatter.FormatArchitecture(RuntimeInformation.OSArchitecture),
            PhysicalCores: physicalCores,
            LogicalProcessors: Environment.ProcessorCount,
            BaseClockMhz: baseMhz,
            MaxClockMhz: smbios.ProcessorMaxSpeedMhz,
            Socket: smbios.ProcessorSocket,
            L2CacheBytes: l2Bytes > 0 ? l2Bytes : null,
            L3CacheBytes: l3Bytes > 0 ? l3Bytes : null);
    }

    private static string? MapCpuVendor(string? raw) => raw switch
    {
        null => null,
        "GenuineIntel" => "Intel",
        "AuthenticAMD" => "AMD",
        _ => raw
    };

    private static (int PhysicalCores, long L2CacheBytes, long L3CacheBytes) ReadLogicalProcessorDetails()
    {
        int cores = 0;
        long l2 = 0, l3 = 0;

        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref len);
            if (len == 0)
                return (0, 0, 0);

            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationAll, buf, ref len))
                    return (0, 0, 0);

                var offset = 0;
                while (offset + 8 <= (int)len)
                {
                    var relationship = Marshal.ReadInt32(buf, offset);
                    var size = Marshal.ReadInt32(buf, offset + 4);
                    if (size <= 0 || offset + size > (int)len)
                        break;

                    if (relationship == RelationProcessorCore)
                    {
                        cores++;
                    }
                    else if (relationship == RelationCache && size >= 16)
                    {
                        var level = Marshal.ReadByte(buf, offset + 8);
                        var cacheSize = (uint)Marshal.ReadInt32(buf, offset + 12);
                        if (level == 2) l2 += cacheSize;
                        else if (level == 3) l3 += cacheSize;
                    }

                    offset += size;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            // Safe fallback.
        }

        return (cores, l2, l3);
    }

    // =========================================================================
    // Motherboard & BIOS
    // =========================================================================

    private static MotherboardInfo ReadMotherboardInfo(SmbiosData smbios) => new(
        Manufacturer: smbios.BaseboardManufacturer ?? smbios.SystemManufacturer,
        Model: smbios.BaseboardProduct,
        Version: smbios.BaseboardVersion,
        SystemManufacturer: smbios.SystemManufacturer,
        SystemModel: smbios.SystemProductName,
        BiosVendor: smbios.BiosVendor,
        BiosVersion: smbios.BiosVersion,
        BiosReleaseDate: smbios.BiosReleaseDate);

    // =========================================================================
    // Memory (RAM)
    // =========================================================================

    private static MemoryInfo ReadMemoryInfo(SmbiosData smbios)
    {
        long totalUsable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        try
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memStatus) && memStatus.ullTotalPhys > 0)
                totalUsable = (long)memStatus.ullTotalPhys;
        }
        catch
        {
            // Fallback already set.
        }

        var modules = smbios.MemoryModules;
        long? installed = modules.Count > 0 ? modules.Sum(m => m.CapacityBytes) : null;
        var memType = modules.Select(m => m.MemoryType).FirstOrDefault(t => t is not null);
        var speed = modules.Select(m => m.SpeedMhz).FirstOrDefault(s => s is > 0);

        return new MemoryInfo(
            TotalUsableBytes: totalUsable,
            InstalledBytes: installed,
            MemoryType: memType,
            SpeedMhz: speed,
            TotalSlots: smbios.TotalMemorySlots > 0 ? smbios.TotalMemorySlots : null,
            UsedSlots: smbios.TotalMemorySlots > 0 ? smbios.UsedMemorySlots : null,
            Modules: modules);
    }

    // =========================================================================
    // GPU (DXGI + Display Class Registry)
    // =========================================================================

    private sealed record RegistryGpuInfo(string Name, string? DriverVersion, string? DriverDate, long? VramBytes);

    private static IReadOnlyList<GpuInfo> ReadGpuInfo()
    {
        var regGpus = ReadRegistryGpus();
        var dxgiGpus = ReadDxgiGpus();

        if (dxgiGpus.Count > 0)
        {
            var result = new List<GpuInfo>(dxgiGpus.Count);
            foreach (var gpu in dxgiGpus)
            {
                var regMatch = FindMatchingRegistryGpu(regGpus, gpu.Name);
                result.Add(gpu with
                {
                    DriverVersion = regMatch?.DriverVersion,
                    DriverDate = regMatch?.DriverDate,
                    DedicatedVramBytes = gpu.DedicatedVramBytes ?? regMatch?.VramBytes
                });
            }

            return result
                .OrderByDescending(g => g.DedicatedVramBytes ?? 0)
                .ToList();
        }

        return regGpus
            .Select(r => new GpuInfo(
                Name: r.Name,
                Vendor: InferGpuVendor(r.Name),
                DedicatedVramBytes: r.VramBytes,
                SharedMemoryBytes: null,
                DriverVersion: r.DriverVersion,
                DriverDate: r.DriverDate))
            .OrderByDescending(g => g.DedicatedVramBytes ?? 0)
            .ToList();
    }

    private static RegistryGpuInfo? FindMatchingRegistryGpu(List<RegistryGpuInfo> regGpus, string dxgiName)
    {
        var exact = regGpus.FirstOrDefault(r => string.Equals(r.Name, dxgiName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return regGpus.FirstOrDefault(r =>
            r.Name.Contains(dxgiName, StringComparison.OrdinalIgnoreCase) ||
            dxgiName.Contains(r.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static unsafe List<GpuInfo> ReadDxgiGpus()
    {
        var hardware = new List<GpuInfo>();
        var software = new List<GpuInfo>();
        IntPtr factory = IntPtr.Zero;

        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero)
                return [];

            var factoryVtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)factoryVtbl[12];

            for (uint i = 0; ; i++)
            {
                var hr = enumAdapters1(factory, i, out var adapter);
                if (hr != 0 || adapter == IntPtr.Zero)
                    break;

                try
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, out DXGI_ADAPTER_DESC1, int>)adapterVtbl[10];
                    if (getDesc1(adapter, out var desc) == 0)
                    {
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        var isSoftware = (desc.Flags & 2u) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
                        var vram = (long)desc.DedicatedVideoMemory;
                        var shared = (long)desc.SharedSystemMemory;

                        var info = new GpuInfo(
                            Name: name,
                            Vendor: MapPciVendor(desc.VendorId) ?? InferGpuVendor(name),
                            DedicatedVramBytes: vram > 0 ? vram : null,
                            SharedMemoryBytes: shared > 0 ? shared : null,
                            DriverVersion: null,
                            DriverDate: null);

                        if (isSoftware)
                            software.Add(info);
                        else
                            hardware.Add(info);
                    }
                }
                finally
                {
                    MarshalRelease(adapter);
                }
            }
        }
        catch
        {
            return [];
        }
        finally
        {
            if (factory != IntPtr.Zero)
                MarshalRelease(factory);
        }

        return hardware.Count > 0 ? hardware : software;
    }

    private static unsafe void MarshalRelease(IntPtr comPtr)
    {
        if (comPtr == IntPtr.Zero) return;
        var vtbl = *(IntPtr**)comPtr;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        release(comPtr);
    }

    private static List<RegistryGpuInfo> ReadRegistryGpus()
    {
        var list = new List<RegistryGpuInfo>();
        try
        {
            const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var classKey = Registry.LocalMachine.OpenSubKey(classPath);
            if (classKey is null) return list;

            foreach (var subName in classKey.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit))
                    continue;

                using var sub = classKey.OpenSubKey(subName);
                if (sub is null) continue;

                var desc = SmbiosParser.CleanString(sub.GetValue("DriverDesc") as string);
                if (desc is null) continue;

                var version = SmbiosParser.CleanString(sub.GetValue("DriverVersion") as string);
                var date = SmbiosParser.CleanString(sub.GetValue("DriverDate") as string);

                long? vram = null;
                var qwMem = sub.GetValue("HardwareInformation.qwMemorySize");
                if (qwMem is long q && q > 0)
                {
                    vram = q;
                }
                else if (sub.GetValue("HardwareInformation.MemorySize") is int m && m > 0)
                {
                    vram = (uint)m;
                }
                else if (sub.GetValue("HardwareInformation.MemorySize") is byte[] bytes && bytes.Length >= 4)
                {
                    var val = bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : BitConverter.ToUInt32(bytes, 0);
                    if (val > 0) vram = val;
                }

                list.Add(new RegistryGpuInfo(desc, version, date, vram));
            }
        }
        catch
        {
            // Ignore registry access failures.
        }
        return list;
    }

    private static string? MapPciVendor(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x5143 => "Qualcomm",
        0x1414 => "Microsoft",
        0x15AD => "VMware",
        _ => null
    };

    private static string? InferGpuVendor(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return "Intel";
        return null;
    }

    // =========================================================================
    // Storage Drives
    // =========================================================================

    private static IReadOnlyList<StorageDriveInfo> ReadStorageInfo()
    {
        var drivesByDisk = new Dictionary<int, List<string>>();
        var driveSizesByLetter = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;

                var root = d.Name.TrimEnd('\\');
                if (root.Length < 2 || root[1] != ':')
                    continue;

                try
                {
                    if (d.IsReady)
                        driveSizesByLetter[root] = d.TotalSize;
                }
                catch { }

                foreach (var diskNum in GetVolumeDiskNumbers(root[..2]))
                {
                    if (!drivesByDisk.TryGetValue(diskNum, out var list))
                    {
                        list = [];
                        drivesByDisk[diskNum] = list;
                    }
                    if (!list.Contains(root, StringComparer.OrdinalIgnoreCase))
                        list.Add(root);
                }
            }
        }
        catch
        {
            // Continue probing PhysicalDrive0..15 anyway.
        }

        var diskIndices = new SortedSet<int>(drivesByDisk.Keys);
        for (var i = 0; i < 16; i++)
            diskIndices.Add(i);

        var result = new List<StorageDriveInfo>();
        foreach (var diskIndex in diskIndices)
        {
            var info = ProbePhysicalDrive(
                diskIndex,
                drivesByDisk.GetValueOrDefault(diskIndex) ?? [],
                driveSizesByLetter);
            if (info is not null)
                result.Add(info);
        }

        return result;
    }

    private static List<int> GetVolumeDiskNumbers(string driveColon)
    {
        var result = new List<int>();
        try
        {
            using var handle = CreateFileW($@"\\.\{driveColon}", 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return result;

            var buf = new byte[256];
            if (!DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buf, buf.Length, out var returned, IntPtr.Zero) || returned < 4)
                return result;

            var count = BitConverter.ToInt32(buf, 0);
            for (var i = 0; i < count; i++)
            {
                var offset = 8 + i * 24;
                if (offset + 4 <= returned)
                    result.Add(BitConverter.ToInt32(buf, offset));
            }
        }
        catch
        {
            // Ignore locked/unmounted volumes.
        }
        return result;
    }

    private static StorageDriveInfo? ProbePhysicalDrive(
        int index,
        List<string> logicalDrives,
        Dictionary<string, long> driveSizes)
    {
        try
        {
            using var handle = CreateFileW($@"\\.\PhysicalDrive{index}", 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return null;

            var (vendor, product, firmware, busType, removable) = QueryStorageDeviceDescriptor(handle);
            var seekPenalty = QuerySeekPenalty(handle);
            var capacity = QueryDiskSize(handle);

            if (capacity <= 0 && logicalDrives.Count > 0)
            {
                long sum = 0;
                foreach (var ld in logicalDrives)
                {
                    if (driveSizes.TryGetValue(ld, out var sz))
                        sum += sz;
                }
                capacity = sum;
            }

            var model = BuildDriveModel(vendor, product) ?? $"Physical Drive {index}";
            var iface = MapBusType(busType);
            var mediaType = DeriveMediaType(busType, seekPenalty, removable);

            logicalDrives.Sort(StringComparer.OrdinalIgnoreCase);

            return new StorageDriveInfo(
                Model: model,
                CapacityBytes: capacity,
                MediaType: mediaType,
                Interface: iface,
                FirmwareVersion: firmware,
                LogicalDrives: logicalDrives);
        }
        catch
        {
            return null;
        }
    }

    private static (string? Vendor, string? Product, string? Firmware, int BusType, bool Removable) QueryStorageDeviceDescriptor(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery { PropertyId = 0, QueryType = 0 };
        var buf = new byte[1024];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<StoragePropertyQuery>(), buf, buf.Length, out var returned, IntPtr.Zero) || returned < 36)
            return (null, null, null, 0, false);

        var removable = buf[11] != 0;
        var vendorOffset = BitConverter.ToInt32(buf, 12);
        var productOffset = BitConverter.ToInt32(buf, 16);
        var revOffset = BitConverter.ToInt32(buf, 20);
        var busType = BitConverter.ToInt32(buf, 28);

        return (
            ReadNullTerminatedAscii(buf, vendorOffset, returned),
            ReadNullTerminatedAscii(buf, productOffset, returned),
            ReadNullTerminatedAscii(buf, revOffset, returned),
            busType,
            removable);
    }

    private static bool? QuerySeekPenalty(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery { PropertyId = 7, QueryType = 0 };
        var ok = DeviceIoControl(handle, IoctlStorageQueryProperty,
            ref query, Marshal.SizeOf<StoragePropertyQuery>(),
            out DeviceSeekPenaltyDescriptor result, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
            out _, IntPtr.Zero);
        return ok ? result.IncursSeekPenalty != 0 : null;
    }

    private static long QueryDiskSize(SafeFileHandle handle)
    {
        var buf = new byte[64];
        if (DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, buf, buf.Length, out var returned, IntPtr.Zero) && returned >= 32)
            return BitConverter.ToInt64(buf, 24);
        return 0;
    }

    private static string? BuildDriveModel(string? vendor, string? product)
    {
        if (vendor is null) return product;
        if (product is null) return vendor;
        if (product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            return product;
        return $"{vendor} {product}";
    }

    private static string? MapBusType(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 or 15 => "Virtual",
        17 => "NVMe",
        _ => null
    };

    private static string DeriveMediaType(int busType, bool? seekPenalty, bool removable)
    {
        if (busType == 17) return "NVMe SSD";
        if (removable || busType == 7) return "Removable";
        return seekPenalty switch
        {
            false => "SSD",
            true => "HDD",
            null => "Disk"
        };
    }

    private static string? ReadNullTerminatedAscii(byte[] buf, int offset, int length)
    {
        if (offset <= 0 || offset >= length)
            return null;
        var end = Array.IndexOf(buf, (byte)0, offset, length - offset);
        if (end < 0) end = length;
        if (end <= offset) return null;
        return SmbiosParser.CleanString(Encoding.ASCII.GetString(buf, offset, end - offset));
    }

    // =========================================================================
    // Displays
    // =========================================================================

    private static unsafe IReadOnlyList<DisplayInfo> ReadDisplayInfo()
    {
        var displays = new List<DisplayInfo>();
        try
        {
            const uint qdcOnlyActivePaths = 2;
            if (GetDisplayConfigBufferSizes(qdcOnlyActivePaths, out var pathCount, out var modeCount) == 0 && pathCount > 0)
            {
                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                if (QueryDisplayConfig(qdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) == 0)
                {
                    for (var i = 0; i < pathCount; i++)
                    {
                        ref readonly var path = ref paths[i];
                        var srcIdx = path.sourceInfo.modeInfoIdx;
                        int width = 0, height = 0;
                        bool isPrimary = false;

                        if (srcIdx < modeCount && modes[srcIdx].infoType == 1) // Source
                        {
                            width = (int)modes[srcIdx].width;
                            height = (int)modes[srcIdx].height;
                            isPrimary = modes[srcIdx].posX == 0 && modes[srcIdx].posY == 0;
                        }

                        double? refresh = null;
                        if (path.targetInfo.refreshRateDenominator > 0 && path.targetInfo.refreshRateNumerator > 0)
                            refresh = Math.Round((double)path.targetInfo.refreshRateNumerator / path.targetInfo.refreshRateDenominator, 2);

                        var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                        {
                            headerType = 2, // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
                            headerSize = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                            adapterId = path.targetInfo.adapterId,
                            id = path.targetInfo.id
                        };

                        string? friendly = null;
                        uint tech = path.targetInfo.outputTechnology;
                        if (DisplayConfigGetDeviceInfo(ref targetName) == 0)
                        {
                            friendly = SmbiosParser.CleanString(new string(targetName.monitorFriendlyDeviceName).TrimEnd('\0'));
                            tech = targetName.outputTechnology;
                        }

                        var conn = MapOutputTechnology(tech);
                        if (friendly is null)
                            friendly = IsInternalDisplay(tech) ? "Built-in Display" : $"Monitor {i + 1}";

                        if (width > 0 && height > 0)
                            displays.Add(new DisplayInfo(friendly, width, height, refresh, isPrimary, conn));
                    }
                }
            }
        }
        catch
        {
            // Fall through to EnumDisplaySettings.
        }

        if (displays.Count == 0)
            displays.AddRange(ReadDisplaysFallback());

        return displays
            .OrderByDescending(d => d.IsPrimary)
            .ToList();
    }

    private static List<DisplayInfo> ReadDisplaysFallback()
    {
        var list = new List<DisplayInfo>();
        try
        {
            var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevicesW(null, i, ref dev, 0); i++)
            {
                if ((dev.StateFlags & 1) == 0) continue; // DISPLAY_DEVICE_ACTIVE
                var isPrimary = (dev.StateFlags & 4) != 0; // DISPLAY_DEVICE_PRIMARY_DEVICE

                var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
                if (EnumDisplaySettingsExW(dev.DeviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref mode, 0))
                {
                    list.Add(new DisplayInfo(
                        Name: SmbiosParser.CleanString(dev.DeviceString) ?? $"Display {list.Count + 1}",
                        Width: (int)mode.dmPelsWidth,
                        Height: (int)mode.dmPelsHeight,
                        RefreshRateHz: mode.dmDisplayFrequency > 0 ? mode.dmDisplayFrequency : null,
                        IsPrimary: isPrimary,
                        ConnectionType: null));
                }
            }
        }
        catch { }
        return list;
    }

    private static bool IsInternalDisplay(uint tech) =>
        tech is 0x80000000 or 6 or 11; // INTERNAL, LVDS, DISPLAYPORT_EMBEDDED

    private static string? MapOutputTechnology(uint tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 => "DisplayPort",
        11 => "eDP (Internal)",
        12 => "UDI",
        13 => "UDI Embedded",
        15 => "Miracast",
        0x80000000 => "Internal",
        _ => null
    };

    // =========================================================================
    // Native Interop
    // =========================================================================

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationAll = 0xFFFF;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint width;
        [FieldOffset(20)] public uint height;
        [FieldOffset(28)] public int posX;
        [FieldOffset(32)] public int posY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public uint headerType;
        public uint headerSize;
        public LUID adapterId;
        public uint id;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        public fixed char monitorFriendlyDeviceName[64];
        public fixed char monitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableID, IntPtr pFirmwareTableBuffer, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, int inBufferSize,
        [Out] byte[] outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        ref StoragePropertyQuery inBuffer, int inBufferSize,
        [Out] byte[] outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        ref StoragePropertyQuery inBuffer, int inBufferSize,
        out DeviceSeekPenaltyDescriptor outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsExW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);
}
