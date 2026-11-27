using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Grepdesk.Core.SystemInfo;

namespace Grepdesk.Linux;

public sealed class LinuxSystemInfoProvider : ISystemInfoProvider
{
    public SystemInfoSnapshot GetSystemInfo()
    {
        var arch = SystemInfoFormatter.FormatArchitecture(RuntimeInformation.OSArchitecture);
        var (osName, osVersion) = ReadOsRelease();
        var cpu = ReadCpuInfo(arch);
        var motherboard = ReadDmiInfo();
        var memory = ReadMemoryInfo();
        var gpus = ReadGpus();
        var storage = ReadStorageDrives();

        return new SystemInfoSnapshot(
            CollectedAtUtc: DateTime.UtcNow,
            Os: new OsSystemInfo(osName, osVersion, arch, Environment.MachineName, ReadUptime()),
            Cpu: cpu,
            Motherboard: motherboard,
            Memory: memory,
            Gpus: gpus,
            StorageDrives: storage,
            Displays: []);
    }

    private static (string Name, string? Version) ReadOsRelease()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
                return ParseOsRelease(File.ReadAllText("/etc/os-release"));
        }
        catch { }
        return (RuntimeInformation.OSDescription.Trim(), null);
    }

    public static (string Name, string? Version) ParseOsRelease(string content)
    {
        string? pretty = null, name = null, version = null;
        foreach (var rawLine in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = rawLine.IndexOf('=');
            if (eq <= 0) continue;
            var key = rawLine[..eq].Trim();
            var val = rawLine[(eq + 1)..].Trim().Trim('"');
            if (key == "PRETTY_NAME") pretty = val;
            else if (key == "NAME") name = val;
            else if (key == "VERSION_ID") version = val;
        }
        return (pretty ?? name ?? "Linux", version);
    }

    private static TimeSpan ReadUptime()
    {
        try
        {
            if (File.Exists("/proc/uptime"))
            {
                var first = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    return TimeSpan.FromSeconds(seconds);
            }
        }
        catch { }
        return TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64));
    }

    private static CpuInfo ReadCpuInfo(string arch)
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
                return ParseCpuInfo(File.ReadAllText("/proc/cpuinfo"), arch, Environment.ProcessorCount);
        }
        catch { }

        return new CpuInfo($"{RuntimeInformation.ProcessArchitecture} Processor", null, arch,
            Environment.ProcessorCount, Environment.ProcessorCount, null, null, null, null, null);
    }

    public static CpuInfo ParseCpuInfo(string content, string arch, int defaultLogicalCount)
    {
        string? model = null, vendor = null;
        int? cores = null;
        int? mhz = null;
        var logical = 0;

        foreach (var line in content.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();

            if (key == "processor")
                logical++;
            else if (key is "model name" or "Hardware" && model is null)
                model = SmbiosParser.CleanString(val);
            else if (key == "vendor_id" && vendor is null)
                vendor = val switch { "GenuineIntel" => "Intel", "AuthenticAMD" => "AMD", _ => SmbiosParser.CleanString(val) };
            else if (key == "cpu cores" && cores is null && int.TryParse(val, out var c) && c > 0)
                cores = c;
            else if (key == "cpu MHz" && mhz is null && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0)
                mhz = (int)Math.Round(m);
        }

        if (logical == 0) logical = defaultLogicalCount;
        return new CpuInfo(
            Name: model ?? "Processor",
            Manufacturer: vendor,
            Architecture: arch,
            PhysicalCores: cores ?? logical,
            LogicalProcessors: logical,
            BaseClockMhz: mhz,
            MaxClockMhz: null,
            Socket: null,
            L2CacheBytes: null,
            L3CacheBytes: null);
    }

    private static MotherboardInfo ReadDmiInfo()
    {
        string? ReadDmi(string file)
        {
            try
            {
                var path = Path.Combine("/sys/class/dmi/id", file);
                return File.Exists(path) ? SmbiosParser.CleanString(File.ReadAllText(path)) : null;
            }
            catch { return null; }
        }

        return new MotherboardInfo(
            Manufacturer: ReadDmi("board_vendor") ?? ReadDmi("sys_vendor"),
            Model: ReadDmi("board_name"),
            Version: ReadDmi("board_version"),
            SystemManufacturer: ReadDmi("sys_vendor"),
            SystemModel: ReadDmi("product_name"),
            BiosVendor: ReadDmi("bios_vendor"),
            BiosVersion: ReadDmi("bios_version"),
            BiosReleaseDate: ReadDmi("bios_date"));
    }

    private static MemoryInfo ReadMemoryInfo()
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                var parsed = ParseMemInfo(File.ReadAllText("/proc/meminfo"));
                if (parsed > 0) total = parsed;
            }
        }
        catch { }

        return new MemoryInfo(total, null, null, null, null, null, []);
    }

    public static long ParseMemInfo(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                continue;
            var parts = line["MemTotal:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], out var kb))
                return kb * 1024L;
        }
        return 0;
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var output = RunCommand("lspci", ["-mm"]);
        return output is null ? [] : ParseLspciGpus(output);
    }

    public static IReadOnlyList<GpuInfo> ParseLspciGpus(string lspciOutput)
    {
        var list = new List<GpuInfo>();
        foreach (var line in lspciOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("VGA compatible controller", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("3D controller", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Display controller", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = SplitQuoted(line);
            if (parts.Count >= 3)
            {
                var vendor = SmbiosParser.CleanString(parts[1]);
                var device = SmbiosParser.CleanString(parts[2]);
                var name = device ?? vendor ?? "GPU";
                list.Add(new GpuInfo(name, vendor, null, null, null, null));
            }
        }
        return list;
    }

    private static List<string> SplitQuoted(string line)
    {
        var res = new List<string>();
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (!inQuotes) { inQuotes = true; start = i + 1; }
                else { inQuotes = false; res.Add(line[start..i]); }
            }
        }
        return res;
    }

    private static IReadOnlyList<StorageDriveInfo> ReadStorageDrives()
    {
        var list = new List<StorageDriveInfo>();
        try
        {
            const string sysBlock = "/sys/block";
            if (!Directory.Exists(sysBlock)) return list;

            foreach (var dir in Directory.EnumerateDirectories(sysBlock))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("loop", StringComparison.Ordinal) ||
                    name.StartsWith("ram", StringComparison.Ordinal) ||
                    name.StartsWith("dm-", StringComparison.Ordinal) ||
                    name.StartsWith("sr", StringComparison.Ordinal))
                    continue;

                var sizeText = ReadFileTrimmed(Path.Combine(dir, "size"));
                if (!long.TryParse(sizeText, out var sectors) || sectors <= 0)
                    continue;

                var capacity = sectors * 512L;
                var model = SmbiosParser.CleanString(ReadFileTrimmed(Path.Combine(dir, "device", "model"))) ?? name;
                var removable = ReadFileTrimmed(Path.Combine(dir, "removable")) == "1";
                var rotational = ReadFileTrimmed(Path.Combine(dir, "queue", "rotational"));
                var isNvme = name.StartsWith("nvme", StringComparison.Ordinal);

                var mediaType = isNvme ? "NVMe SSD"
                    : removable ? "Removable"
                    : rotational == "0" ? "SSD"
                    : rotational == "1" ? "HDD"
                    : "Disk";

                list.Add(new StorageDriveInfo(model, capacity, mediaType, isNvme ? "NVMe" : null, null, [$"/dev/{name}"]));
            }
        }
        catch { }
        return list;
    }

    private static string? ReadFileTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static string? RunCommand(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var task = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? task.Result : null;
        }
        catch { return null; }
    }
}
