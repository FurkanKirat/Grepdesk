using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Grepdesk.UI;

/// <summary>
/// Loads UI strings from Assets/Lang/{code}.json based on the system's
/// current UI culture, with an English fallback. Access strings via
/// Localization.Instance["SomeKey"] or the static Localization.Instance.Get("SomeKey").
/// </summary>
public sealed class LocalizationService
{
    private const string FallbackLanguage = "en";
    private static readonly string LangDirectory =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Lang");

    private Dictionary<string, string> _strings = new();

    public static LocalizationService Instance { get; } = new();

    public string CurrentLanguage { get; private set; } = FallbackLanguage;

    private LocalizationService()
    {
        Load(DetectSystemLanguage());
    }

    /// <summary>
    /// Reads the two-letter ISO language code from the OS via .NET's
    /// globalization APIs. Works the same way on Windows, Linux, and macOS —
    /// .NET reads the platform's language setting under the hood
    /// (Windows display language, Linux LANG/LC_ALL, macOS system locale).
    /// </summary>
    public static string DetectSystemLanguage()
    {
        try
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }
        catch
        {
            return FallbackLanguage;
        }
    }

    public void Load(string languageCode)
    {
        var path = Path.Combine(LangDirectory, $"{languageCode}.json");

        if (!File.Exists(path))
        {
            path = Path.Combine(LangDirectory, $"{FallbackLanguage}.json");
            languageCode = FallbackLanguage;
        }

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                           ?? new Dictionary<string, string>();
            }
            else
            {
                _strings = new Dictionary<string, string>();
            }

            CurrentLanguage = languageCode;
        }
        catch
        {
            // Corrupt or unreadable file — fall back to empty dictionary
            // rather than crashing app startup.
            _strings = new Dictionary<string, string>();
        }
    }

    /// <summary>Gets a localized string, or the key itself if missing (visible fallback for debugging).</summary>
    public string Get(string key) => _strings.TryGetValue(key, out var value) ? value : key;

    /// <summary>Gets a localized string and applies string.Format with the given args.</summary>
    public string Get(string key, params object[] args) => string.Format(Get(key), args);

    public string this[string key] => Get(key);
}