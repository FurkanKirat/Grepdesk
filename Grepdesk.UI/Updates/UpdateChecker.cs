using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Grepdesk.UI.Updates;

internal enum UpdateStatus { UpToDate, Available, Failed }

/// <param name="Version">"1.2.0", without the tag's "v".</param>
/// <param name="Url">The release page, where the installer is attached.</param>
internal sealed record UpdateResult(UpdateStatus Status, string? Version = null, string? Url = null);

/// <summary>
/// Asks GitHub for the latest published release and compares it with the
/// running version. Nothing is downloaded or installed: the user gets a link
/// to the release page.
/// </summary>
internal sealed class UpdateChecker(HttpClient http)
{
    private const string LatestReleaseApi = "https://api.github.com/repos/FurkanKirat/Grepdesk/releases/latest";

    public static UpdateChecker CreateDefault() =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

    public async Task<UpdateResult> CheckAsync(string currentVersion, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            // GitHub rejects requests without a User-Agent.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Grepdesk", currentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateResult(UpdateStatus.UpToDate); // no release published yet
            if (!response.IsSuccessStatusCode)
                return new UpdateResult(UpdateStatus.Failed);

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var tag = json.RootElement.GetProperty("tag_name").GetString();
            var url = json.RootElement.GetProperty("html_url").GetString();
            if (tag is null || url is null || ParseVersion(tag) is not { } latest)
                return new UpdateResult(UpdateStatus.Failed);

            return ParseVersion(currentVersion) is { } current && latest > current
                ? new UpdateResult(UpdateStatus.Available, latest.ToString(3), url)
                : new UpdateResult(UpdateStatus.UpToDate);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            // Offline, timed out, or an unexpected answer: try again another day.
            if (ct.IsCancellationRequested) throw;
            return new UpdateResult(UpdateStatus.Failed);
        }
    }

    /// <summary>"v1.2" or "1.2.0-beta+abc" → 1.2.0; null if it isn't a version.</summary>
    internal static Version? ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0) core = core[..cut];
        if (!Version.TryParse(core, out var version)) return null;
        // Normalize "1.2" to 1.2.0 so it compares equal to "1.2.0".
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    internal static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) is { } a && ParseVersion(current) is { } b && a > b;
}
