using System.Text;

namespace Grepdesk.Core.Browse;

/// <summary>
/// Reads where a Windows shortcut (.lnk) points, straight from the file ([MS-SHLLINK]), so no COM is
/// needed and it can be tested anywhere. Shortcuts that name their target only by a shell item
/// (Control Panel, "This PC"…) have no path in them; those give null.
/// </summary>
public static class ShortcutFile
{
    private const int HeaderSize = 0x4C;
    private const int MaxBytes = 1024 * 1024; // a real .lnk is a few KB

    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint VolumeIdAndLocalBasePath = 0x1;
    private const uint CommonNetworkRelativeLinkAndPathSuffix = 0x2;

    /// <summary>The target path stored in the shortcut, or null if there is none or the file is not a valid shortcut.</summary>
    public static string? ReadTarget(string lnkPath)
    {
        try
        {
            var info = new FileInfo(lnkPath);
            if (!info.Exists || info.Length < HeaderSize || info.Length > MaxBytes) return null;
            return Parse(File.ReadAllBytes(lnkPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The folder a shortcut opens, if it points at one that exists.</summary>
    public static string? ResolveFolder(string lnkPath)
    {
        var target = ReadTarget(lnkPath);
        if (target is null) return null;
        try
        {
            target = Environment.ExpandEnvironmentVariables(target);
            return Directory.Exists(target) ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static string? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || U32(data, 0) != HeaderSize) return null;
        var flags = U32(data, 0x14);

        var pos = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (pos + 2 > data.Length) return null;
            pos += 2 + U16(data, pos); // the item-id list: a shell item path, not a file path
        }
        if ((flags & HasLinkInfo) == 0 || pos + 0x1C > data.Length) return null;

        var link = data[pos..];
        var size = U32(link, 0);
        if (size < 0x1C || size > link.Length) return null;
        link = link[..(int)size];

        var headerSize = U32(link, 4);
        var linkInfoFlags = U32(link, 8);

        if ((linkInfoFlags & VolumeIdAndLocalBasePath) != 0)
        {
            string? basePath = null, suffix = null;
            if (headerSize >= 0x24 && link.Length >= 0x24)
            {
                // Newer shortcuts carry the same two strings in UTF-16 as well; those are exact.
                basePath = Utf16(link, U32(link, 0x1C));
                suffix = Utf16(link, U32(link, 0x20));
            }
            basePath ??= Ansi(link, U32(link, 0x10));
            suffix ??= Ansi(link, U32(link, 0x18));
            return basePath is null ? null : Join(basePath, suffix);
        }

        if ((linkInfoFlags & CommonNetworkRelativeLinkAndPathSuffix) != 0)
        {
            var netOffset = U32(link, 0x14);
            if (netOffset == 0 || netOffset + 0x14 > link.Length) return null;
            var net = link[(int)netOffset..];
            var netName = Ansi(net, U32(net, 8)); // \\server\share
            var suffix = Ansi(link, U32(link, 0x18));
            return netName is null ? null : Join(netName, suffix);
        }
        return null;
    }

    private static string Join(string basePath, string? suffix) =>
        string.IsNullOrEmpty(suffix) ? basePath : Path.Combine(basePath, suffix);

    private static uint U32(ReadOnlySpan<byte> d, int offset) =>
        offset >= 0 && offset + 4 <= d.Length ? BitConverter.ToUInt32(d.Slice(offset, 4)) : 0;

    private static ushort U16(ReadOnlySpan<byte> d, int offset) =>
        offset >= 0 && offset + 2 <= d.Length ? BitConverter.ToUInt16(d.Slice(offset, 2)) : (ushort)0;

    /// <summary>A zero-terminated single-byte string at <paramref name="offset"/>, or null when the offset is 0 or out of range.</summary>
    private static string? Ansi(ReadOnlySpan<byte> d, uint offset)
    {
        if (offset == 0 || offset >= d.Length) return null;
        var rest = d[(int)offset..];
        var end = rest.IndexOf((byte)0);
        return end < 0 ? null : Encoding.Latin1.GetString(rest[..end]);
    }

    private static string? Utf16(ReadOnlySpan<byte> d, uint offset)
    {
        if (offset == 0 || offset >= d.Length) return null;
        var rest = d[(int)offset..];
        for (var i = 0; i + 1 < rest.Length; i += 2)
            if (rest[i] == 0 && rest[i + 1] == 0) return Encoding.Unicode.GetString(rest[..i]);
        return null;
    }
}
