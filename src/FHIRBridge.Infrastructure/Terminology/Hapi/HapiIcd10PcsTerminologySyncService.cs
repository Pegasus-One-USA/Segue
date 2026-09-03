using System.IO.Compression;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CMS ICD-10-PCS order file and loads it into the embedded HAPI FHIR
/// terminology server as a CodeSystem resource. Mirrors <see cref="HapiIcd10TerminologySyncService"/>
/// (ICD-10-CM) almost exactly — same CMS fixed-width "order file" layout — with two differences:
///
/// 1. CMS publishes a new dated zip URL each fiscal year (no stable permanent link, like HCPCS), so
///    this first scrapes the ICD-10 codes listing page for the most recent PCS order-file zip.
/// 2. ICD-10-PCS codes are always exactly 7 characters with no decimal point (e.g. "0016070"),
///    unlike ICD-10-CM which needs a "." inserted after the 3rd character.
///
/// Record layout (1-indexed, identical to the CM order file): 1-5 order number; 7-13 code (7 chars,
/// no decimal); 15 valid-for-submission flag ("1"=billable); 17-76 short description; 77+ long
/// description. Confirmed against the real FY2027 release.
/// </summary>
public sealed class HapiIcd10PcsTerminologySyncService : IHapiIcd10PcsTerminologySyncService
{
    private const string ListingPageUrl = "https://www.cms.gov/medicare/coding-billing/icd-10-codes";
    private const string SystemUrl = "http://www.cms.gov/Medicare/Coding/ICD10";

    private static readonly Regex ZipLinkPattern = new(
        @"href=""(/files/zip/[a-z0-9\-]*icd-10-pcs-order-file[a-z0-9\-]*\.zip)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiIcd10PcsTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiIcd10PcsTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiIcd10PcsTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiIcd10PcsSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiIcd10PcsTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        _logger.LogInformation("Finding the latest official CMS ICD-10-PCS order file.");
        var zipUrl = await FindLatestZipUrlAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Downloading ICD-10-PCS release from {ZipUrl}.", zipUrl);

        var concepts = await DownloadAndParseAsync(downloadClient, zipUrl, cancellationToken);
        var billableCount = concepts.Count(c => c.Billable);
        _logger.LogInformation(
            "Parsed {Total} ICD-10-PCS codes ({Billable} billable) from the official release.", concepts.Count, billableCount);
        await _localWriter.WriteConceptsAsync(
            SystemUrl, "ICD10PCS", version: null, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "ICD-10-PCS loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiIcd10PcsSyncResult(concepts.Count, billableCount, stopwatch.Elapsed);
    }

    private static async Task<string> FindLatestZipUrlAsync(HttpClient http, CancellationToken ct)
    {
        var html = await http.GetStringAsync(ListingPageUrl, ct);
        var match = ZipLinkPattern.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not find an ICD-10-PCS order-file zip link on {ListingPageUrl}. The page layout may have changed.");
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
        var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Contains("pcs_order", StringComparison.OrdinalIgnoreCase)
                && e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Could not find the ICD-10-PCS order-file .txt entry in the downloaded zip.");

        var results = new List<Concept>(85_000);
        using var reader = new StreamReader(entry.Open());
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length < 77)
            {
                continue;
            }

            var code = line.Substring(6, 7).Trim();
            var billable = line.Substring(14, 1) == "1";
            var longDescription = line.Substring(76).Trim();

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(longDescription))
            {
                continue;
            }

            results.Add(new Concept(code, longDescription, billable));
        }

        return results;
    }

    private sealed record Concept(string Code, string Display, bool Billable);
}
