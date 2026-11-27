namespace Grepdesk.Core.SystemInfo;

public interface ISystemInfoProvider
{
    SystemInfoSnapshot GetSystemInfo();
}

public sealed record SystemInfoSnapshot(
    DateTime CollectedAtUtc,
    OsSystemInfo Os,
    CpuInfo Cpu,
    MotherboardInfo Motherboard,
    MemoryInfo Memory,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<StorageDriveInfo> StorageDrives,
    IReadOnlyList<DisplayInfo> Displays);

public sealed record OsSystemInfo(
    string OsName,
    string? OsVersion,
    string Architecture,
    string MachineName,
    TimeSpan Uptime);

public sealed record CpuInfo(
    string Name,
    string? Manufacturer,
    string Architecture,
    int PhysicalCores,
    int LogicalProcessors,
    int? BaseClockMhz,
    int? MaxClockMhz,
    string? Socket,
    long? L2CacheBytes,
    long? L3CacheBytes);

public sealed record GpuInfo(
    string Name,
    string? Vendor,
    long? DedicatedVramBytes,
    long? SharedMemoryBytes,
    string? DriverVersion,
    string? DriverDate);

public sealed record MemoryInfo(
    long TotalUsableBytes,
    long? InstalledBytes,
    string? MemoryType,
    int? SpeedMhz,
    int? TotalSlots,
    int? UsedSlots,
    IReadOnlyList<MemoryModuleInfo> Modules);

public sealed record MemoryModuleInfo(
    string? Slot,
    long CapacityBytes,
    string? MemoryType,
    int? SpeedMhz,
    string? Manufacturer,
    string? PartNumber,
    string? FormFactor);

public sealed record StorageDriveInfo(
    string Model,
    long CapacityBytes,
    string MediaType,
    string? Interface,
    string? FirmwareVersion,
    IReadOnlyList<string> LogicalDrives);

public sealed record MotherboardInfo(
    string? Manufacturer,
    string? Model,
    string? Version,
    string? SystemManufacturer,
    string? SystemModel,
    string? BiosVendor,
    string? BiosVersion,
    string? BiosReleaseDate);

public sealed record DisplayInfo(
    string Name,
    int Width,
    int Height,
    double? RefreshRateHz,
    bool IsPrimary,
    string? ConnectionType);
