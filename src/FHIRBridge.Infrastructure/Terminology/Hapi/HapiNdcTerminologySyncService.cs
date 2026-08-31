using System.IO.Compression;
using System.Net.Http.Json;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official FDA National Drug Code (NDC) directory and loads it into the
/// embedded HAPI FHIR terminology server as a CodeSystem resource. Mirrors
/// <see cref="HapiIcd10TerminologySyncService"/>'s shape.
///
/// Source: https://www.accessdata.fda.gov/cder/ndctext.zip — public, no credentials. Contains
/// product.txt, a tab-delimited file with a header row. Column 2 (PRODUCTNDC) is the code; column 4
/// (PROPRIETARYNAME, the brand name) is used as display, falling back to column 6
/// (NONPROPRIETARYNAME, the generic name) when the brand name is blank. Confirmed reachable and
/// ~115,000 rows as of 2026-08-25.
/// </summary>
public sealed class HapiNdcTerminologySyncService : IHapiNdcTerminologySyncService
{
    private const string ZipUrl = "https://www.accessdata.fda.gov/cder/ndctext.zip";
    private const string ProductFileEntryName = "product.txt";
    private const string SystemUrl = "http://hl7.org/fhir/sid/ndc";
    private const string ResourceId = "ndc-full";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiNdcTerminologySyncService> _logger;
    private readonly HapiTerminologyServerClient _serverClient;

    public HapiNdcTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiNdcTerminologySyncService> logger,
        HapiTerminologyServerClient serverClient)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _serverClient = serverClient;
    }

    public async Task<HapiNdcSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Downloading official NDC directory from the FDA.");
        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiNdcTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(5);

        var concepts = await DownloadAndParseAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Parsed {Total} NDC product codes from the official directory.", concepts.Count);
        var resource = BuildCodeSystemResource(concepts);
        await _serverClient.PutCodeSystemAsync(ResourceId, resource, concepts.Count, TimeSpan.FromMinutes(15), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "NDC loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiNdcSyncResult(concepts.Count, stopwatch.Elapsed);
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, CancellationToken ct)
    {
        await using var zipBytes = await http.GetStreamAsync(ZipUrl, ct);
        using var memory = new MemoryStream();
        await zipBytes.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = archive.GetEntry(ProductFileEntryName)
            ?? throw new InvalidOperationException(
                $"Expected entry '{ProductFileEntryName}' was not found in the downloaded NDC directory zip.");

        // Keyed by code to dedupe: the real FDA file has ~900 PRODUCTNDC values repeated across
        // multiple product records (package-size variants, repackagers, etc.) — a CodeSystem can't
        // contain duplicate concept codes, so first-seen wins.
        var byCode = new Dictionary<string, string>(120_000, StringComparer.Ordinal);
        using var reader = new StreamReader(entry.Open());

        var header = await reader.ReadLineAsync(ct); // skip header row
        if (header is null)
        {
            return [];
        }

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            var fields = line.Split('\t');
            if (fields.Length < 6)
            {
                continue;
            }

            var code = fields[1].Trim(); // PRODUCTNDC
            var brandName = fields[3].Trim(); // PROPRIETARYNAME
            var genericName = fields[5].Trim(); // NONPROPRIETARYNAME
            var display = !string.IsNullOrEmpty(brandName) ? brandName : genericName;

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(display))
            {
                continue;
            }

            byCode.TryAdd(code, display);
        }

        return byCode.Select(kv => new Concept(kv.Key, kv.Value)).ToList();
    }

    private static object BuildCodeSystemResource(IReadOnlyList<Concept> concepts)
    {
        return new
        {
            resourceType = "CodeSystem",
            id = ResourceId,
            url = SystemUrl,
            name = "NDC",
            title = "National Drug Code Directory (auto-synced from FDA)",
            status = "active",
            content = "complete",
            count = concepts.Count,
            concept = concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }

    private sealed record Concept(string Code, string Display);
}
