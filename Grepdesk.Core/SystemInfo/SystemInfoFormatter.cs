using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Grepdesk.Core.SystemInfo;

public static class SystemInfoFormatter
{
    public static string FormatBinarySize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;
        const double tb = gb * 1024.0;

        if (bytes >= tb) return $"{bytes / tb:0.##} TB";
        if (bytes >= gb) return $"{bytes / gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / mb:0.##} MB";
        if (bytes >= kb) return $"{bytes / kb:0.#} KB";
        return $"{bytes} B";
    }

    public static string FormatStorageCapacity(long bytes)
    {
        if (bytes <= 0) return "0 B";
        const double kb = 1000.0;
        const double mb = kb * 1000.0;
        const double gb = mb * 1000.0;
        const double tb = gb * 1000.0;

        if (bytes >= tb) return $"{bytes / tb:0.##} TB";
        if (bytes >= gb) return $"{bytes / gb:0.#} GB";
        if (bytes >= mb) return $"{bytes / mb:0.#} MB";
        if (bytes >= kb) return $"{bytes / kb:0.#} KB";
        return $"{bytes} B";
    }

    public static string FormatCpuClock(int mhz)
    {
        if (mhz <= 0) return "—";
        if (mhz >= 1000)
            return string.Create(CultureInfo.InvariantCulture, $"{mhz / 1000.0:0.00} GHz");
        return $"{mhz} MHz";
    }

    public static string FormatMemorySpeed(int mhz, string? memoryType)
    {
        if (mhz <= 0) return "—";
        var isDdr = memoryType is not null &&
            memoryType.Contains("DDR", StringComparison.OrdinalIgnoreCase);
        return isDdr ? $"{mhz} MT/s" : $"{mhz} MHz";
    }

    public static string FormatArchitecture(Architecture arch) => arch switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "ARM64",
        Architecture.Arm => "ARM",
        _ => arch.ToString()
    };

    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        var days = (int)uptime.TotalDays;
        if (days > 0)
            return $"{days}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.Hours > 0)
            return $"{uptime.Hours}h {uptime.Minutes}m";
        return $"{uptime.Minutes}m";
    }

    public static string FormatPlainText(SystemInfoSnapshot info, bool detailed = false)
    {
        var sb = new StringBuilder();

        // CPU
        sb.AppendLine($"CPU:         {info.Cpu.Name}");
        if (detailed)
        {
            if (info.Cpu.Manufacturer is not null)
                sb.AppendLine($"  Vendor:    {info.Cpu.Manufacturer}");
            sb.AppendLine($"  Cores:     {info.Cpu.PhysicalCores} cores / {info.Cpu.LogicalProcessors} threads ({info.Cpu.Architecture})");
            if (info.Cpu.BaseClockMhz is { } baseMhz)
                sb.AppendLine($"  Base Clk:  {FormatCpuClock(baseMhz)}");
            if (info.Cpu.MaxClockMhz is { } maxMhz)
                sb.AppendLine($"  Max Clk:   {FormatCpuClock(maxMhz)}");
            if (info.Cpu.Socket is not null)
                sb.AppendLine($"  Socket:    {info.Cpu.Socket}");
            if (info.Cpu.L2CacheBytes is { } l2)
                sb.AppendLine($"  L2 Cache:  {FormatBinarySize(l2)}");
            if (info.Cpu.L3CacheBytes is { } l3)
                sb.AppendLine($"  L3 Cache:  {FormatBinarySize(l3)}");
        }

        // GPU
        if (info.Gpus.Count == 0)
        {
            sb.AppendLine("GPU:         Unknown");
        }
        else
        {
            for (var i = 0; i < info.Gpus.Count; i++)
            {
                var gpu = info.Gpus[i];
                var vram = gpu.DedicatedVramBytes is > 0 ? $" ({FormatBinarySize(gpu.DedicatedVramBytes.Value)})" : "";
                var prefix = i == 0 ? "GPU:         " : "             ";
                sb.AppendLine($"{prefix}{gpu.Name}{vram}");
                if (detailed)
                {
                    if (gpu.Vendor is not null)
                        sb.AppendLine($"  Vendor:    {gpu.Vendor}");
                    if (gpu.SharedMemoryBytes is > 0)
                        sb.AppendLine($"  Shared:    {FormatBinarySize(gpu.SharedMemoryBytes.Value)}");
                    if (gpu.DriverVersion is not null)
                    {
                        var date = gpu.DriverDate is not null ? $" ({gpu.DriverDate})" : "";
                        sb.AppendLine($"  Driver:    {gpu.DriverVersion}{date}");
                    }
                }
            }
        }

        // RAM
        var ramTotal = FormatBinarySize(info.Memory.InstalledBytes ?? info.Memory.TotalUsableBytes);
        var ramType = info.Memory.MemoryType is not null ? $" {info.Memory.MemoryType}" : "";
        var ramSpeed = info.Memory.SpeedMhz is > 0 ? $" @ {FormatMemorySpeed(info.Memory.SpeedMhz.Value, info.Memory.MemoryType)}" : "";
        sb.AppendLine($"RAM:         {ramTotal}{ramType}{ramSpeed}");
        if (detailed)
        {
            sb.AppendLine($"  Usable:    {FormatBinarySize(info.Memory.TotalUsableBytes)}");
            if (info.Memory.TotalSlots is > 0 && info.Memory.UsedSlots is not null)
                sb.AppendLine($"  Slots:     {info.Memory.UsedSlots} / {info.Memory.TotalSlots}");
            foreach (var mod in info.Memory.Modules)
            {
                var slot = mod.Slot ?? "Slot";
                var cap = FormatBinarySize(mod.CapacityBytes);
                var mfg = mod.Manufacturer is not null ? $" {mod.Manufacturer}" : "";
                var part = mod.PartNumber is not null ? $" ({mod.PartNumber})" : "";
                var modSpd = mod.SpeedMhz is > 0 ? $" @ {FormatMemorySpeed(mod.SpeedMhz.Value, mod.MemoryType)}" : "";
                var ff = mod.FormFactor is not null ? $" [{mod.FormFactor}]" : "";
                sb.AppendLine($"  - {slot}: {cap}{mfg}{part}{modSpd}{ff}");
            }
        }

        // Storage
        if (info.StorageDrives.Count == 0)
        {
            sb.AppendLine("Storage:     Unknown");
        }
        else
        {
            for (var i = 0; i < info.StorageDrives.Count; i++)
            {
                var d = info.StorageDrives[i];
                var cap = d.CapacityBytes > 0 ? FormatStorageCapacity(d.CapacityBytes) : "";
                var meta = string.Join(", ", new[] { cap, d.MediaType }.Where(s => !string.IsNullOrEmpty(s)));
                var drives = d.LogicalDrives.Count > 0 ? $" [{string.Join(", ", d.LogicalDrives)}]" : "";
                var prefix = i == 0 ? "Storage:     " : "             ";
                sb.AppendLine($"{prefix}{d.Model} ({meta}){drives}");
                if (detailed)
                {
                    if (d.Interface is not null)
                        sb.AppendLine($"  Interface: {d.Interface}");
                    if (d.FirmwareVersion is not null)
                        sb.AppendLine($"  Firmware:  {d.FirmwareVersion}");
                }
            }
        }

        // Motherboard & BIOS
        var mbParts = new[] { info.Motherboard.Manufacturer, info.Motherboard.Model ?? info.Motherboard.SystemModel }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var mbSummary = string.Join(" ", mbParts);
        if (!string.IsNullOrEmpty(mbSummary) || info.Motherboard.BiosVersion is not null)
        {
            sb.AppendLine($"Motherboard: {(string.IsNullOrEmpty(mbSummary) ? "Unknown" : mbSummary)}");
            if (detailed)
            {
                if (info.Motherboard.Version is not null)
                    sb.AppendLine($"  Version:   {info.Motherboard.Version}");
                if (info.Motherboard.SystemModel is not null && info.Motherboard.SystemModel != info.Motherboard.Model)
                    sb.AppendLine($"  System:    {info.Motherboard.SystemManufacturer} {info.Motherboard.SystemModel}".TrimEnd());
                if (info.Motherboard.BiosVendor is not null || info.Motherboard.BiosVersion is not null)
                {
                    var bios = string.Join(" ", new[] { info.Motherboard.BiosVendor, info.Motherboard.BiosVersion }.Where(s => s is not null));
                    var date = info.Motherboard.BiosReleaseDate is not null ? $" ({info.Motherboard.BiosReleaseDate})" : "";
                    sb.AppendLine($"  BIOS:      {bios}{date}");
                }
            }
        }

        // Displays
        if (info.Displays.Count > 0)
        {
            for (var i = 0; i < info.Displays.Count; i++)
            {
                var disp = info.Displays[i];
                var hz = disp.RefreshRateHz is > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" @ {disp.RefreshRateHz.Value:0.##} Hz")
                    : "";
                var primary = disp.IsPrimary ? " [Primary]" : "";
                var prefix = i == 0 ? "Display:     " : "             ";
                sb.AppendLine($"{prefix}{disp.Name} — {disp.Width}x{disp.Height}{hz}{primary}");
                if (detailed && disp.ConnectionType is not null)
                    sb.AppendLine($"  Output:    {disp.ConnectionType}");
            }
        }

        // OS
        var osVer = info.Os.OsVersion is not null ? $" {info.Os.OsVersion}" : "";
        sb.AppendLine($"OS:          {info.Os.OsName}{osVer} ({info.Os.Architecture})");
        if (detailed)
        {
            sb.AppendLine($"  Host:      {info.Os.MachineName}");
            sb.AppendLine($"  Uptime:    {FormatUptime(info.Os.Uptime)}");
        }

        return sb.ToString().TrimEnd();
    }
}
