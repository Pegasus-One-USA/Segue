using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CMS HCPCS Level II quarterly release and loads it into the embedded
/// HAPI FHIR terminology server as a CodeSystem resource. Mirrors <see cref="HapiIcd10TerminologySyncService"/>'s
/// shape, with two differences from the simpler vocabularies:
///
/// 1. CMS publishes a new dated zip URL every quarter (e.g. "october-2026-alpha-numeric-hcpcs-file.zip")
///    with no stable permanent link, so this first scrapes the listing page for the most recent one.
/// 2. The fixed-width "ANWEB" data file wraps long descriptions across multiple physical rows sharing
///    the same HCPCS code (a "sequence number" field, positions 6-10, increments by 100 per
///    continuation row) — reconstructing the full description requires concatenating consecutive
///    same-code rows' description chunks with a single space, per CMS's own documented record layout
///    (HCPC&lt;year&gt;_recordlayout.txt in the release zip).
///
/// Record layout (1-indexed): 1-5 HCPCS code; 6-10 sequence number; 12-91 long description chunk (80
/// chars, word-wrapped, no mid-word splits). Confirmed against the real October 2026 release.
/// </summary>
public sealed class HapiHcpcsTerminologySyncService : IHapiHcpcsTerminologySyncService
{
    private const string ListingPageUrl =
        "https://www.cms.gov/medicare/coding-billing/healthcare-common-procedure-system/quarterly-update";
    private const string SystemUrl = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    private const string ResourceId = "hcpcs-full";

    private static readonly Regex ZipLinkPattern = new(
        @"href=""(/files/zip/[a-z0-9\-]*alpha-numeric-hcpcs-file[a-z0-9\-]*\.zip)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiHcpcsTerminologySyncService> _logger;

    public HapiHcpcsTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiHcpcsTerminologySyncService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    public async Task<HapiHcpcsSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Shared setting across every vocabulary — see HapiIcd10TerminologySyncService's remarks.
        var configuredDefault = _configuration["Terminology:BaseUrl"] ?? "http://hapi-terminology:8080/fhir";
        var serverBaseUrl = (await _settings.GetStringAsync(
            "Terminology:BaseUrl", configuredDefault, cancellationToken)).TrimEnd('/');

        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiHcpcsTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        _logger.LogInformation("Finding the latest official CMS HCPCS Level II quarterly release.");
        var zipUrl = await FindLatestZipUrlAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Downloading HCPCS release from {ZipUrl}.", zipUrl);

        var concepts = await DownloadAndParseAsync(downloadClient, zipUrl, cancellationToken);
        _logger.LogInformation("Parsed {Total} HCPCS codes from the official release.", concepts.Count);

        // Bypasses IHttpClientFactory — see HapiIcd10TerminologySyncService's remarks on the
        // app-wide resilience default stacking with, rather than being replaced by, a named override.
        using var serverClient = new HttpClient
        {
            BaseAddress = new Uri(serverBaseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };

        var resource = BuildCodeSystemResource(concepts);
        var response = await serverClient.PutAsJsonAsync($"CodeSystem/{ResourceId}", resource, cancellationToken);
        response.EnsureSuccessStatusCode();

        stopwatch.Stop();
        _logger.LogInformation(
            "HCPCS loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiHcpcsSyncResult(concepts.Count, stopwatch.Elapsed);
    }

    private static async Task<string> FindLatestZipUrlAsync(HttpClient http, CancellationToken ct)
    {
        var html = await http.GetStringAsync(ListingPageUrl, ct);
        var match = ZipLinkPattern.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not find an HCPCS release zip link on {ListingPageUrl}. The page layout may have changed.");
        }

        return "https://www.cms.gov" + match.Groups[1].Value;
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, string zipUrl, CancellationToken ct)
    {
        await using var zipBytes = await http.GetStreamAsync(zipUrl, ct);
        using var memory = new MemoryStream();
        await zipBytes.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith("_ANWEB.txt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Could not find the '*_ANWEB.txt' data file in the downloaded HCPCS release zip.");

        var results = new List<Concept>(20_000);
        string? currentCode = null;
        var currentChunks = new List<string>();

        void FlushCurrent()
        {
            if (currentCode is null || currentChunks.Count == 0)
            {
                return;
            }

            var display = string.Join(' ', currentChunks.Select(c => c.Trim()).Where(c => c.Length > 0));
            if (!string.IsNullOrEmpty(display))
            {
                results.Add(new Concept(currentCode, display));
            }
        }

        using var reader = new StreamReader(entry.Open());
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            // Continuation lines are NOT padded to the full 320-char record width — a line holding
            // only the tail of a wrapped description can be as short as ~12-40 chars. Only the code
            // field (0-5) needs to be reliably present; the description chunk is whatever remains
            // past position 11, however short.
            if (line.Length < 5)
            {
                continue;
            }

            var code = line.Substring(0, 5).Trim();
            var descriptionChunk = line.Length > 11 ? line.Substring(11, Math.Min(80, line.Length - 11)) : string.Empty;
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            if (code != currentCode)
            {
                FlushCurrent();
                currentCode = code;
                currentChunks = new List<string>();
            }

            currentChunks.Add(descriptionChunk);
        }

        FlushCurrent();
        return results;
    }

    private static object BuildCodeSystemResource(IReadOnlyList<Concept> concepts)
    {
        return new
        {
            resourceType = "CodeSystem",
            id = ResourceId,
            url = SystemUrl,
            name = "HCPCS",
            title = "HCPCS Level II (auto-synced from CMS)",
            status = "active",
            content = "complete",
            count = concepts.Count,
            concept = concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }

    private sealed record Concept(string Code, string Display);
}
