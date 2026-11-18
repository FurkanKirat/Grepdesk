using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Grepdesk.Core.Browse;

/// <summary>
/// Parses and builds virtual paths inside <c>.zip</c> archives, such as
/// <c>C:\docs\bundle.zip</c> or <c>C:\docs\bundle.zip\src\images</c>.
/// </summary>
public static class ZipBrowsePath
{
    public static bool IsZipExtension(string? path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static bool IsZipFile(string? path) =>
        IsZipExtension(path) && File.Exists(path);

    public static bool IsZipPath(string? path) =>
        !string.IsNullOrEmpty(path) && TrySplit(path, out _, out _);

    /// <summary>
    /// Splits a virtual zip path into the archive file on disk (<paramref name="archivePath"/>)
    /// and the normalized forward-slash inner path (<paramref name="innerPath"/>, empty at root).
    /// </summary>
    public static bool TrySplit(string path, out string archivePath, out string innerPath)
    {
        archivePath = "";
        innerPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = PathUtil.Normalize(path);
        if (IsZipFile(normalized))
        {
            archivePath = normalized;
            return true;
        }

        const string marker = ".zip";
        var searchStart = 0;
        while (searchStart < normalized.Length)
        {
            var idx = normalized.IndexOf(marker, searchStart, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            var endOfExt = idx + marker.Length;
            if (endOfExt < normalized.Length &&
                (normalized[endOfExt] == Path.DirectorySeparatorChar || normalized[endOfExt] == Path.AltDirectorySeparatorChar))
            {
                var candidate = normalized[..endOfExt];
                if (File.Exists(candidate))
                {
                    archivePath = candidate;
                    innerPath = normalized[(endOfExt + 1)..]
                        .Replace('\\', '/')
                        .Trim('/');
                    return true;
                }
            }
            searchStart = endOfExt;
        }

        return false;
    }

    public static string Combine(string archivePath, string innerRelativePath)
    {
        var clean = innerRelativePath.Replace('/', Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return clean.Length == 0 ? archivePath : Path.Combine(archivePath, clean);
    }
}

/// <summary>Reads virtual folder listings and previews from inside <c>.zip</c> archives.</summary>
public static class ZipFolderListing
{
    private sealed class DirAccum
    {
        public long Size;
        public DateTime Modified;
        public FileAttributes Attributes = FileAttributes.Directory | FileAttributes.ReadOnly;
    }

    public static FolderContents List(string zipBrowsePath, bool showHidden = false, CancellationToken ct = default)
    {
        if (!ZipBrowsePath.TrySplit(zipBrowsePath, out var archivePath, out var innerPath))
            return new FolderContents(zipBrowsePath, [], FolderListingError.NotFound);

        var prefix = innerPath.Length == 0 ? "" : innerPath + "/";
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var files = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
            var dirs = new Dictionary<string, DirAccum>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var full = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (full.Length == 0) continue;

                if (prefix.Length > 0)
                {
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    full = full[prefix.Length..];
                }
                if (full.Length == 0) continue;

                var isWindowsHidden = (entry.ExternalAttributes & (int)FileAttributes.Hidden) != 0;
                var slash = full.IndexOf('/');
                if (slash < 0)
                {
                    // Direct child file
                    if (!showHidden && isWindowsHidden) continue;
                    var childInner = prefix + full;
                    var virtualPath = ZipBrowsePath.Combine(archivePath, childInner);
                    var attrs = FileAttributes.ReadOnly | (isWindowsHidden ? FileAttributes.Hidden : 0);
                    files[full] = new FolderEntry(virtualPath, full, false, entry.Length, entry.LastWriteTime.LocalDateTime, attrs);
                }
                else
                {
                    var dirName = full[..slash];
                    if (dirName.Length == 0) continue;
                    if (!dirs.TryGetValue(dirName, out var acc))
                    {
                        acc = new DirAccum();
                        dirs[dirName] = acc;
                    }
                    if (!full.EndsWith('/'))
                        acc.Size += entry.Length;
                    var dt = entry.LastWriteTime.LocalDateTime;
                    if (dt > acc.Modified) acc.Modified = dt;
                    if (full.Length == slash + 1 && isWindowsHidden)
                        acc.Attributes |= FileAttributes.Hidden;
                }
            }

            var entries = new List<FolderEntry>(dirs.Count + files.Count);
            foreach (var (dirName, acc) in dirs)
            {
                if (!showHidden && (acc.Attributes & FileAttributes.Hidden) != 0) continue;
                var childInner = prefix + dirName;
                entries.Add(new FolderEntry(
                    ZipBrowsePath.Combine(archivePath, childInner),
                    dirName,
                    IsDirectory: true,
                    acc.Size,
                    acc.Modified,
                    acc.Attributes));
            }
            entries.AddRange(files.Values);
            return new FolderContents(zipBrowsePath, entries);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return new FolderContents(zipBrowsePath, [], FolderListingError.NotFound); }
        catch (DirectoryNotFoundException) { return new FolderContents(zipBrowsePath, [], FolderListingError.NotFound); }
        catch (UnauthorizedAccessException) { return new FolderContents(zipBrowsePath, [], FolderListingError.AccessDenied); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new FolderContents(zipBrowsePath, [], FolderListingError.Failed);
        }
    }

    public static bool DirectoryExists(string archivePath, string innerPath)
    {
        if (innerPath.Length == 0) return File.Exists(archivePath);
        var prefix = innerPath.Trim('/') + "/";
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var full = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* unreadable archive */ }
        return false;
    }

    public static bool EntryExists(string archivePath, string innerPath)
    {
        if (innerPath.Length == 0) return false;
        var target = innerPath.Trim('/');
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var full = entry.FullName.Replace('\\', '/').Trim('/');
                if (!entry.FullName.EndsWith('/') && string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* unreadable archive */ }
        return false;
    }

    /// <summary>
    /// Extracts a single file entry from a <c>.zip</c> virtual path to a temporary file
    /// so it can be previewed or opened in the viewer.
    /// </summary>
    public static string? TryExtractEntryToTemp(string virtualPath, string? tempRoot = null)
    {
        if (!ZipBrowsePath.TrySplit(virtualPath, out var archivePath, out var innerPath) || innerPath.Length == 0)
            return null;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? match = null;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var full = entry.FullName.Replace('\\', '/').Trim('/');
                if (string.Equals(full, innerPath, StringComparison.OrdinalIgnoreCase))
                {
                    match = entry;
                    break;
                }
            }
            if (match is null) return null;

            var root = tempRoot ?? Path.Combine(Path.GetTempPath(), "Grepdesk", "zip-browse");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{archivePath}|{innerPath}")))[..16];
            var dir = Path.Combine(root, key);
            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(innerPath);
            if (string.IsNullOrEmpty(fileName)) fileName = "entry";
            var dest = Path.Combine(dir, fileName);

            var expectedTime = match.LastWriteTime.UtcDateTime;
            if (File.Exists(dest))
            {
                var info = new FileInfo(dest);
                if (info.Length == match.Length && Math.Abs((info.LastWriteTimeUtc - expectedTime).TotalSeconds) < 2)
                    return dest;
            }

            match.ExtractToFile(dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, expectedTime);
            return dest;
        }
        catch
        {
            return null;
        }
    }
}
