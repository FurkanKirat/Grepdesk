using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Grepdesk.Core.ContentSearch;

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

    public async Task<string?> ExtractTextAsync(string path, CancellationToken ct) =>
        (await ExtractDetailedAsync(path, ct)).Text;

    public async Task<ExtractionResult> ExtractDetailedAsync(string path, CancellationToken ct)
    {
        try
        {
            return ExtractionResult.Ok(await File.ReadAllTextAsync(path, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExtractionResult.Fail(SkipReason.AccessDenied, ex.Message);
        }
        catch (IOException ex)
        {
            return ExtractionResult.Fail(SkipClassifier.FromIoException(ex), ex.Message);
        }
    }
}
