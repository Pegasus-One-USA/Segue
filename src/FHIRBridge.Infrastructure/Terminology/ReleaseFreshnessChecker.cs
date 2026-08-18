using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;

namespace FHIRBridge.Infrastructure.Terminology;

/// <inheritdoc cref="IReleaseFreshnessChecker"/>
public sealed partial class ReleaseFreshnessChecker : IReleaseFreshnessChecker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISystemSettingsCache _settings;
    private readonly ISystemSettingsService _settingsService;

    public ReleaseFreshnessChecker(IHttpClientFactory httpClientFactory, ISystemSettingsCache settings, ISystemSettingsService settingsService)
        => (_httpClientFactory, _settings, _settingsService) = (httpClientFactory, settings, settingsService);

    public async Task<ReleaseFreshnessResult> CheckAsync(string listingUrl, string settingsKeyPrefix, IReadOnlyList<string> requiredKeywords, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(ReleaseFreshnessChecker));
        var html = await client.GetStringAsync(listingUrl, cancellationToken);
        var baseUri = new Uri(listingUrl);

        // Capture the actual href, not just a bare file name — on a flat FTP directory listing (ICD-10-CM) the
        // two are the same, but on a CMS HTML page (ICD-10-PCS/HCPCS) the real download path can differ from
        // the visible file name, so resolving against baseUri here (not at download time) is what makes the
        // stored URL actually fetchable later.
        var candidates = HrefFileReferencePattern().Matches(html)
            .Select(m => m.Groups[1].Value)
            .Where(href => Uri.TryCreate(baseUri, href, out _))
            .Select(href => new Uri(baseUri, href))
            .Distinct()
            // These listing pages mix the real release with unrelated auxiliary files (addenda, guideline PDFs,
            // POA-exempt-code lists, etc.) — require every configured keyword to appear in the file name so a
            // candidate like "POAexemptCodesFY26.zip" can't outrank the actual release just because it sorts
            // later alphabetically.
            .Where(uri => requiredKeywords.All(keyword => Path.GetFileName(uri.LocalPath).Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            // Plain alphabetical sort silently picks the wrong file across a year boundary — confirmed live:
            // "october-2025-...zip" sorts after "july-2026-...zip" alphabetically ('o' > 'j') despite being the
            // OLDER release. Extract a 4-digit year (and a month name/abbreviation, if present) so releases sort
            // chronologically first, falling back to plain string order only as a last-resort tiebreaker.
            .OrderByDescending(uri => ExtractYear(Path.GetFileName(uri.LocalPath)))
            .ThenByDescending(uri => ExtractMonth(Path.GetFileName(uri.LocalPath)))
            .ThenByDescending(uri => Path.GetFileName(uri.LocalPath), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var latestUri = candidates.FirstOrDefault();
        var latestFileName = latestUri is null ? null : Path.GetFileName(latestUri.LocalPath);

        var lastKnown = await _settings.GetStringAsync($"Terminology:{settingsKeyPrefix}:LastKnownFileName", string.Empty, cancellationToken);
        var isNewer = !string.IsNullOrWhiteSpace(latestFileName) && !string.Equals(latestFileName, lastKnown, StringComparison.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        await _settingsService.SetAsync($"Terminology:{settingsKeyPrefix}:LastCheckedUtc", now.ToString("O"), "Last time a release-freshness check ran.", cancellationToken);
        if (latestUri is not null)
        {
            await _settingsService.SetAsync($"Terminology:{settingsKeyPrefix}:LastKnownFileName", latestFileName!, "Most recent release file name observed at the source listing.", cancellationToken);
            await _settingsService.SetAsync($"Terminology:{settingsKeyPrefix}:LastKnownDownloadUrl", latestUri.ToString(), "Resolved absolute URL for the most recent release file — used by Download & Import.", cancellationToken);
        }

        return new ReleaseFreshnessResult(isNewer, latestFileName, now);
    }

    public async Task<ReleaseFreshnessResult?> GetLastCheckAsync(string settingsKeyPrefix, CancellationToken cancellationToken)
    {
        var lastCheckedText = await _settings.GetStringAsync($"Terminology:{settingsKeyPrefix}:LastCheckedUtc", string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(lastCheckedText) || !DateTime.TryParse(lastCheckedText, out var lastChecked)) return null;
        var lastKnown = await _settings.GetStringAsync($"Terminology:{settingsKeyPrefix}:LastKnownFileName", string.Empty, cancellationToken);
        return new ReleaseFreshnessResult(false, string.IsNullOrWhiteSpace(lastKnown) ? null : lastKnown, lastChecked.ToUniversalTime());
    }

    public async Task<string> DownloadLatestAsync(string settingsKeyPrefix, string destinationFolderName, CancellationToken cancellationToken)
    {
        var downloadUrlText = await _settings.GetStringAsync($"Terminology:{settingsKeyPrefix}:LastKnownDownloadUrl", string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(downloadUrlText) || !Uri.TryCreate(downloadUrlText, UriKind.Absolute, out var downloadUrl))
            throw new InvalidOperationException("No release has been identified yet — run \"Check for Updates\" first.");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", destinationFolderName);
        Directory.CreateDirectory(root);
        var fileName = Path.GetFileName(downloadUrl.LocalPath) is { Length: > 0 } name ? name : $"{destinationFolderName}-{Guid.NewGuid():N}.zip";
        var path = Path.Combine(root, fileName);

        var client = _httpClientFactory.CreateClient(nameof(ReleaseFreshnessChecker));
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(path))
            await source.CopyToAsync(destination, cancellationToken);

        return path;
    }

    // Matches an href attribute whose value ends in .zip/.txt/.csv — allows a single embedded space (e.g.
    // "icd10cm-Code Descriptions-2026.zip") since CDC/CMS file names aren't always space-free.
    [GeneratedRegex(@"href\s*=\s*[""']([^""']+\.(?:zip|txt|csv))[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefFileReferencePattern();

    private static readonly IReadOnlyDictionary<string, int> MonthNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["jan"] = 1, ["february"] = 2, ["feb"] = 2, ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4, ["may"] = 5, ["june"] = 6, ["jun"] = 6, ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8, ["september"] = 9, ["sep"] = 9, ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11, ["december"] = 12, ["dec"] = 12,
    };

    private static int ExtractYear(string fileName)
    {
        var match = Regex.Match(fileName, @"(20\d{2})");
        return match.Success ? int.Parse(match.Value) : 0;
    }

    private static int ExtractMonth(string fileName)
    {
        foreach (var (name, number) in MonthNames)
            if (Regex.IsMatch(fileName, $@"(?<![a-z]){Regex.Escape(name)}(?![a-z])", RegexOptions.IgnoreCase))
                return number;
        return 0;
    }
}
