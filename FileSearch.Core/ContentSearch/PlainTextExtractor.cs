using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.ContentSearch;

/// <summary>Handles ordinary text-based formats — no parsing needed, just read the bytes.</summary>
public class PlainTextExtractor : IContentExtractor
{
    public IEnumerable<string> SupportedExtensions =>
    [
        ".txt", ".md", ".log", ".cs", ".py", ".js", ".ts", ".jsx", ".tsx",
        ".json", ".xml", ".html", ".htm", ".csv", ".ini", ".yaml", ".yml",
        ".config", ".toml", ".env", ".sql", ".sh", ".bat", ".ps1",
        ".cpp", ".c", ".h", ".hpp", ".java", ".go", ".rs", ".rb", ".php",
        ".css", ".scss", ".less", ".gradle", ".properties",
        ".csproj", ".sln", ".vue", ".svelte"
    ];

    public async Task<string?> ExtractTextAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
