using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Grepdesk.Tests;

public partial class LocalizationTests
{
    [GeneratedRegex(@"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"(?:\bLoc\.Get\(|\bLocalizationService\.Instance\[)\s*""([A-Za-z][A-Za-z0-9_]*)""\s*(?:,|\)|\])")]
    private static partial Regex LiteralKeyLookupRegex();

    [Fact]
    public void Language_files_have_valid_unique_non_empty_entries()
    {
        var langDir = GetLangDirectory();
        Assert.True(File.Exists(Path.Combine(langDir, "en.json")), "en.json is missing.");
        Assert.True(File.Exists(Path.Combine(langDir, "tr.json")), "tr.json is missing.");

        var files = Directory.GetFiles(langDir, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var entries = LoadLanguageFile(file);
            Assert.NotEmpty(entries);
        }
    }

    [Fact]
    public void All_language_files_have_matching_keys()
    {
        var langDir = GetLangDirectory();
        var baseline = LoadLanguageFile(Path.Combine(langDir, "en.json"));

        foreach (var file in Directory.GetFiles(langDir, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "en.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var other = LoadLanguageFile(file);
            var missing = baseline.Keys.Except(other.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();
            var extra = other.Keys.Except(baseline.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();

            Assert.True(
                missing.Count == 0 && extra.Count == 0,
                $"{name} out of sync with en.json. Missing: [{string.Join(", ", missing)}]; Extra: [{string.Join(", ", extra)}]");
        }
    }

    [Fact]
    public void Format_placeholders_match_across_languages_and_format_cleanly()
    {
        var langDir = GetLangDirectory();
        var baseline = LoadLanguageFile(Path.Combine(langDir, "en.json"));
        var files = Directory.GetFiles(langDir, "*.json");

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var entries = LoadLanguageFile(file);

            foreach (var (key, value) in entries)
            {
                var indices = ExtractPlaceholderIndices(value);
                if (indices.Count > 0)
                {
                    var args = Enumerable.Repeat<object>(123, indices.Max() + 1).ToArray();
                    try
                    {
                        _ = string.Format(CultureInfo.InvariantCulture, value, args);
                    }
                    catch (FormatException ex)
                    {
                        Assert.Fail($"{name} key '{key}' has invalid format string '{value}': {ex.Message}");
                    }
                }

                if (baseline.TryGetValue(key, out var baselineValue))
                {
                    var expected = ExtractPlaceholderIndices(baselineValue);
                    Assert.True(
                        expected.SetEquals(indices),
                        $"{name} key '{key}' placeholders {{{string.Join(",", indices.Order())}}} do not match en.json {{{string.Join(",", expected.Order())}}}.");
                }
            }
        }
    }

    [Fact]
    public void Literal_localization_keys_in_ui_code_exist_in_language_files()
    {
        var repoRoot = FindRepoRoot();
        var uiDir = Path.Combine(repoRoot, "Grepdesk.UI");
        var baseline = LoadLanguageFile(Path.Combine(GetLangDirectory(), "en.json"));
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(uiDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutputPath(uiDir, file))
                continue;

            var source = File.ReadAllText(file);
            foreach (Match match in LiteralKeyLookupRegex().Matches(source))
            {
                var key = match.Groups[1].Value;
                if (!baseline.ContainsKey(key))
                {
                    var rel = Path.GetRelativePath(repoRoot, file);
                    missing.Add($"{rel}: {key}");
                }
            }
        }

        Assert.True(missing.Count == 0, $"Missing localization keys referenced in code:\n{string.Join("\n", missing)}");
    }

    private static HashSet<int> ExtractPlaceholderIndices(string value)
    {
        var stripped = value.Replace("{{", "").Replace("}}", "");
        var indices = new HashSet<int>();
        foreach (Match match in PlaceholderRegex().Matches(stripped))
            indices.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return indices;
    }

    private static Dictionary<string, string> LoadLanguageFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            Assert.True(prop.Value.ValueKind == JsonValueKind.String, $"{fileName} key '{prop.Name}' must have a string value.");
            var text = prop.Value.GetString();
            Assert.False(string.IsNullOrWhiteSpace(text), $"{fileName} key '{prop.Name}' must not be empty.");
            Assert.True(dict.TryAdd(prop.Name, text!), $"{fileName} contains duplicate key '{prop.Name}'.");
        }
        return dict;
    }

    private static bool IsBuildOutputPath(string rootDir, string filePath)
    {
        var relative = Path.GetRelativePath(rootDir, filePath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string GetLangDirectory() =>
        Path.Combine(FindRepoRoot(), "Grepdesk.UI", "Assets", "Lang");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Grepdesk.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root containing Grepdesk.sln.");
    }
}
