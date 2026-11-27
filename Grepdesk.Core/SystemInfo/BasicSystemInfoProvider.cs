using System.Runtime.InteropServices;

namespace Grepdesk.Core.SystemInfo;

/// <summary>
/// Cross-platform fallback provider using standard .NET runtime APIs.
/// </summary>
public sealed class BasicSystemInfoProvider : ISystemInfoProvider
{
    public SystemInfoSnapshot GetSystemInfo()
    {
        var arch = SystemInfoFormatter.FormatArchitecture(RuntimeInformation.OSArchitecture);
        var os = new OsSystemInfo(
            OsName: RuntimeInformation.OSDescription.Trim(),
            OsVersion: Environment.OSVersion.VersionString,
            Architecture: arch,
            MachineName: Environment.MachineName,
            Uptime: TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64)));

        var logical = Environment.ProcessorCount;
        var cpu = new CpuInfo(
            Name: RuntimeInformation.ProcessArchitecture + " Processor",
            Manufacturer: null,
            Architecture: arch,
            PhysicalCores: logical,
            LogicalProcessors: logical,
            BaseClockMhz: null,
            MaxClockMhz: null,
            Socket: null,
            L2CacheBytes: null,
            L3CacheBytes: null);

        var totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var memory = new MemoryInfo(
            TotalUsableBytes: totalMem,
            InstalledBytes: null,
            MemoryType: null,
            SpeedMhz: null,
            TotalSlots: null,
            UsedSlots: null,
            Modules: []);

        var drives = new List<StorageDriveInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                if (!d.IsReady)
                    continue;
                drives.Add(new StorageDriveInfo(
                    Model: string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : d.VolumeLabel,
                    CapacityBytes: d.TotalSize,
                    MediaType: d.DriveType == DriveType.Removable ? "Removable" : "Disk",
                    Interface: null,
                    FirmwareVersion: null,
                    LogicalDrives: [d.Name.TrimEnd('\\', '/')]));
            }
        }
        catch
        {
            // Ignore drive enumeration failures on restricted systems.
        }

        return new SystemInfoSnapshot(
            CollectedAtUtc: DateTime.UtcNow,
            Os: os,
            Cpu: cpu,
            Motherboard: new MotherboardInfo(null, null, null, null, null, null, null, null),
            Memory: memory,
            Gpus: [],
            StorageDrives: drives,
            Displays: []);
    }
}
