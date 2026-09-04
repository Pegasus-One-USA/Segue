using System.IO.Compression;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CMS/CDC ICD-10-CM order file (public, no credentials) and loads
/// it into the embedded HAPI FHIR terminology server as a CodeSystem resource. Ported from the
/// proven tools/TerminologyServerPoc console POC into a reusable, injectable service so a
/// background worker can trigger it on a schedule instead of a person running a command.
///
/// File format is CMS's documented fixed-width "order file" layout (1-indexed):
///   1-5   order number; 7-13 code (no decimal); 15 valid-for-submission flag ("1"=billable);
///   17-76 short description; 77+ long description. See icd-10-cm-order-files.pdf in the release
///   zip. Config: Terminology:BaseUrl (the HAPI terminology server, shared by all vocabularies), defaulting
///   to the in-compose-network address of the hapi-terminology service.
/// </summary>
public sealed class HapiIcd10TerminologySyncService : IHapiIcd10TerminologySyncService
{
    private const string ReleaseYear = "2025";
    private const string ZipUrl =
        "https://ftp.cdc.gov/pub/health_statistics/nchs/Publications/ICD10CM/2025/ICD10-CM%20Code%20Descriptions%202025.zip";
    private const string OrderFileEntryName = "icd10cm-order-2025.txt";
    private const string SystemUrl = "http://hl7.org/fhir/sid/icd-10-cm";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiIcd10TerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiIcd10TerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiIcd10TerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiIcd10SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("Downloading official ICD-10-CM {Year} release from CDC.", ReleaseYear);
        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiIcd10TerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(5);

        var concepts = await DownloadAndParseAsync(downloadClient, cancellationToken);
        var billableCount = concepts.Count(c => c.Billable);
        _logger.LogInformation(
            "Parsed {Total} ICD-10-CM codes ({Billable} billable) from the official release.",
            concepts.Count,
            billableCount);

        await _localWriter.WriteConceptsAsync(
            SystemUrl, "ICD10CM", ReleaseYear, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "ICD-10-CM {Year} loaded into the terminology server: {Total} codes in {Elapsed}.",
            ReleaseYear,
            concepts.Count,
            stopwatch.Elapsed);

        return new HapiIcd10SyncResult(ReleaseYear, concepts.Count, billableCount, stopwatch.Elapsed);
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, CancellationToken ct)
    {
        await using var zipBytes = await http.GetStreamAsync(ZipUrl, ct);
        using var memory = new MemoryStream();
        await zipBytes.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = archive.GetEntry(OrderFileEntryName)
            ?? throw new InvalidOperationException(
                $"Expected entry '{OrderFileEntryName}' was not found in the downloaded ICD-10-CM release zip.");

        var results = new List<Concept>(100_000);
        using var reader = new StreamReader(entry.Open());
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length < 77)
            {
                continue;
            }

            var rawCode = line.Substring(6, 7).Trim();
            var billable = line.Substring(14, 1) == "1";
            var longDescription = line.Substring(76).Trim();

            if (string.IsNullOrEmpty(rawCode) || string.IsNullOrEmpty(longDescription))
            {
                continue;
            }

            var code = rawCode.Length > 3 ? string.Concat(rawCode.AsSpan(0, 3), ".", rawCode.AsSpan(3)) : rawCode;
            results.Add(new Concept(code, longDescription, billable));
        }

        return results;
    }

    private sealed record Concept(string Code, string Display, bool Billable);
}
