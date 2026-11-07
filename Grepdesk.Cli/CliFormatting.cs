using System.Globalization;

namespace Grepdesk.Cli;

internal static class CliFormatting
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb.ToString("0.0", CultureInfo.InvariantCulture)} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb.ToString("0.0", CultureInfo.InvariantCulture)} MB";
        double gb = mb / 1024.0;
        if (gb < 1024) return $"{gb.ToString("0.00", CultureInfo.InvariantCulture)} GB";
        double tb = gb / 1024.0;
        return $"{tb.ToString("0.00", CultureInfo.InvariantCulture)} TB";
    }

    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();
        int unitStart = span.Length;
        while (unitStart > 0 && char.IsLetter(span[unitStart - 1]))
            unitStart--;

        var numberPart = span[..unitStart].Trim();
        var unitPart = span[unitStart..].Trim();

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            return false;

        long multiplier = unitPart.ToString().ToUpperInvariant() switch
        {
            "" or "B" => 1L,
            "K" or "KB" or "KIB" => 1024L,
            "M" or "MB" or "MIB" => 1024L * 1024L,
            "G" or "GB" or "GIB" => 1024L * 1024L * 1024L,
            "T" or "TB" or "TIB" => 1024L * 1024L * 1024L * 1024L,
            _ => -1L
        };

        if (multiplier < 0) return false;
        bytes = (long)(value * multiplier);
        return true;
    }

    public static string RelativeOrFull(string root, string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(root, fullPath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return rel.Replace('\\', '/');
        }
        catch
        {
            // Fallback to full path
        }
        return fullPath.Replace('\\', '/');
    }

    public static bool IsHiddenOrSystem(string fullPath, string rootPath)
    {
        try
        {
            var rel = Path.GetRelativePath(rootPath, fullPath);
            if (rel != "." && !rel.StartsWith("..", StringComparison.Ordinal))
            {
                foreach (var part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    if (part.Length > 1 && part[0] == '.')
                        return true;
                }
            }

            var attrs = File.GetAttributes(fullPath);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static int GetDepth(string rootPath, string fullPath)
    {
        var rel = Path.GetRelativePath(rootPath, fullPath);
        if (rel == "." || string.IsNullOrEmpty(rel)) return 0;
        int depth = 1;
        for (int i = 0; i < rel.Length; i++)
        {
            if (rel[i] == Path.DirectorySeparatorChar || rel[i] == Path.AltDirectorySeparatorChar)
                depth++;
        }
        return depth;
    }
}
