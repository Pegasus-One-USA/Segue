using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official WHO ICD-11 MMS linearization export and loads it into the embedded HAPI
/// FHIR terminology server as a CodeSystem resource. Mirrors <see cref="HapiCvxTerminologySyncService"/>'s
/// shape.
///
/// Source: https://icd.who.int/dev11/Downloads/Download?fileName=LinearizationMiniOutput-MMS-en.zip
/// — WHO's public bulk-download area, no credentials required (unlike the interactive ICD-11
/// entity-browsing API, which needs an OAuth2 client). Contains a tab-delimited .txt with one row
/// per classification entry; only rows with a non-empty "Code" column are real codeable entities
/// (chapters/blocks are organizational groupings with no code of their own, and are skipped). The
/// "Title" column is quote-wrapped and prefixed with a depth indicator like "- - - Cholera" (one
/// "- " per hierarchy level) — both are stripped for the display text.
/// </summary>
public sealed class HapiIcd11TerminologySyncService : IHapiIcd11TerminologySyncService
{
    private const string ZipUrl = "https://icd.who.int/dev11/Downloads/Download?fileName=LinearizationMiniOutput-MMS-en.zip";
    private const string TextFileName = "LinearizationMiniOutput-MMS-en.txt";
    private const string SystemUrl = "http://id.who.int/icd/release/11/mms";
    private const string ResourceId = "icd11-mms-full";

    private static readonly Regex DepthPrefixPattern = new(@"^(?:-\s)+", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiIcd11TerminologySyncService> _logger;
    private readonly HapiTerminologyServerClient _serverClient;

    public HapiIcd11TerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiIcd11TerminologySyncService> logger,
        HapiTerminologyServerClient serverClient)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _serverClient = serverClient;
    }

    public async Task<HapiIcd11SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Downloading official WHO ICD-11 MMS linearization export.");
        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiIcd11TerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        var concepts = await DownloadAndParseAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Parsed {Total} ICD-11 MMS codes from the official release.", concepts.Count);
        var resource = BuildCodeSystemResource(concepts);
        await _serverClient.PutCodeSystemAsync(ResourceId, resource, concepts.Count, TimeSpan.FromMinutes(15), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "ICD-11 MMS loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiIcd11SyncResult(concepts.Count, stopwatch.Elapsed);
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, CancellationToken ct)
    {
        await using var zipBytes = await http.GetStreamAsync(ZipUrl, ct);
        using var memory = new MemoryStream();
        await zipBytes.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = archive.GetEntry(TextFileName)
            ?? throw new InvalidOperationException($"Expected entry '{TextFileName}' was not found in the downloaded ICD-11 MMS zip.");

        // Keyed by code to dedupe: a handful of ICD-11 codes legitimately repeat across different
        // grouping branches of the linearization; first-seen wins, same approach as NDC's dedupe.
        var byCode = new Dictionary<string, string>(40_000, StringComparer.Ordinal);
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
            if (fields.Length < 5)
            {
                continue;
            }

            var code = fields[2].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue; // chapters/blocks are organizational groupings, not codeable entities
            }

            var title = Unquote(fields[4]);
            var display = DepthPrefixPattern.Replace(title, string.Empty).Trim();
            if (string.IsNullOrEmpty(display))
            {
                continue;
            }

            byCode.TryAdd(code, display);
        }

        return byCode.Select(kv => new Concept(kv.Key, kv.Value)).ToList();
    }

    private static string Unquote(string field)
    {
        field = field.Trim();
        return field.Length >= 2 && field[0] == '"' && field[^1] == '"' ? field[1..^1] : field;
    }

    private static object BuildCodeSystemResource(IReadOnlyList<Concept> concepts)
    {
        return new
        {
            resourceType = "CodeSystem",
            id = ResourceId,
            url = SystemUrl,
            name = "ICD11MMS",
            title = "ICD-11 MMS (Mortality and Morbidity Statistics) (auto-synced from WHO)",
            status = "active",
            content = "complete",
            count = concepts.Count,
            concept = concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }

    private sealed record Concept(string Code, string Display);
}
