using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grepdesk.Core.SystemInfo;

namespace Grepdesk.MacOS;

public sealed class MacSystemInfoProvider : ISystemInfoProvider
{
    public SystemInfoSnapshot GetSystemInfo()
    {
        var arch = SystemInfoFormatter.FormatArchitecture(RuntimeInformation.OSArchitecture);
        var osName = Run("sw_vers", ["-productName"]) ?? "macOS";
        var osVersion = Run("sw_vers", ["-productVersion"]);
        var cpuBrand = Run("sysctl", ["-n", "machdep.cpu.brand_string"]) ?? "Apple Silicon";
        var physCores = ParseInt(Run("sysctl", ["-n", "hw.physicalcpu"])) ?? Environment.ProcessorCount;
        var totalMem = ParseLong(Run("sysctl", ["-n", "hw.memsize"])) ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        var profilerJson = Run("system_profiler", ["SPDisplaysDataType", "SPStorageDataType", "-json"]);
        var gpus = profilerJson is not null ? ParseSystemProfilerDisplays(profilerJson) : [];
        var drives = profilerJson is not null ? ParseSystemProfilerStorage(profilerJson) : [];

        return new SystemInfoSnapshot(
            CollectedAtUtc: DateTime.UtcNow,
            Os: new OsSystemInfo(osName, osVersion, arch, Environment.MachineName, TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64))),
            Cpu: new CpuInfo(cpuBrand, null, arch, physCores, Environment.ProcessorCount, null, null, null, null, null),
            Motherboard: new MotherboardInfo("Apple Inc.", Run("sysctl", ["-n", "hw.model"]), null, "Apple Inc.", Run("sysctl", ["-n", "hw.model"]), null, null, null),
            Memory: new MemoryInfo(totalMem, totalMem, null, null, null, null, []),
            Gpus: gpus,
            StorageDrives: drives,
            Displays: []);
    }

    public static IReadOnlyList<GpuInfo> ParseSystemProfilerDisplays(string json)
    {
        var list = new List<GpuInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("sppci_model", out var m) ? m.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var vendor = item.TryGetProperty("spdisplays_vendor", out var v) ? v.GetString() : null;
                list.Add(new GpuInfo(name, vendor, null, null, null, null));
            }
        }
        catch { }
        return list;
    }

    public static IReadOnlyList<StorageDriveInfo> ParseSystemProfilerStorage(string json)
    {
        var list = new List<StorageDriveInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPStorageDataType", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("_name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                long size = item.TryGetProperty("size_in_bytes", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                var mount = item.TryGetProperty("mount_point", out var mp) ? mp.GetString() : null;
                list.Add(new StorageDriveInfo(name, size, "SSD", null, null, mount is not null ? [mount] : []));
            }
        }
        catch { }
        return list;
    }

    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static long? ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? Run(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var task = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? task.Result.Trim() : null;
        }
        catch { return null; }
    }
}
