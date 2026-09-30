namespace Grepdesk.UI.Jobs;

internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : t.TotalSeconds >= 10 ? $"{t.Minutes}:{t.Seconds:00}"
        : $"{t.TotalSeconds:0.0} s";
}
