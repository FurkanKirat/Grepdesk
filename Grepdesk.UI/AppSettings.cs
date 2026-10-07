using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grepdesk.UI;

/// <summary>
/// User preferences, stored as JSON in the per-user app data folder
/// (%APPDATA%\Grepdesk on Windows, ~/.config/Grepdesk on Linux).
/// A missing or unreadable file just means defaults.
/// </summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grepdesk", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Two-letter language code, or null to follow the OS language.</summary>
    public string? Language { get; set; }

    public SortMode Sort { get; set; } = SortMode.NameAsc;

    public bool ShowPreview { get; set; } = true;

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch { /* corrupt file: fall back to defaults rather than failing startup */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* read-only profile etc.: the setting still applies for this session */ }
    }
}
