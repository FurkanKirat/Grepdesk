using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Grepdesk.Core;
using Microsoft.Win32.SafeHandles;

namespace Grepdesk.Windows;

/// <summary>
/// Tells SSDs from spinning disks by asking the storage driver whether the
/// volume "incurs a seek penalty" — the same signal Windows' own defrag
/// scheduler uses. Needs no admin rights (the volume is opened with no access).
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsDriveKindProvider : IDriveKindProvider
{
    private readonly ConcurrentDictionary<string, DriveKind> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BasicDriveKindProvider _basic = new();

    public DriveKind GetKind(string path)
    {
        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return DriveKind.Unknown; }

        return _cache.GetOrAdd(root, r =>
        {
            var basic = _basic.GetKind(r);
            if (basic != DriveKind.Unknown)
                return basic;

            // "C:\" → "\\.\C:"
            if (r.Length < 2 || r[1] != ':')
                return DriveKind.Unknown;

            return QuerySeekPenalty($@"\\.\{r[..2]}") switch
            {
                true => DriveKind.Rotational,
                false => DriveKind.SolidState,
                null => DriveKind.Unknown
            };
        });
    }

    private static bool? QuerySeekPenalty(string volume)
    {
        using var handle = CreateFileW(volume, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
        var ok = DeviceIoControl(handle, IoctlStorageQueryProperty,
            ref query, Marshal.SizeOf<StoragePropertyQuery>(),
            out DeviceSeekPenaltyDescriptor result, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
            out _, IntPtr.Zero);

        // Fails on volumes spanning several disks, some RAID and virtual drives.
        return ok ? result.IncursSeekPenalty != 0 : null;
    }

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        ref StoragePropertyQuery inBuffer, int inBufferSize,
        out DeviceSeekPenaltyDescriptor outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}
