using System.IO.Compression;
using System.Net.Http.Json;
using Microsoft.VisualBasic.FileIO;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official LOINC release and loads it into the embedded HAPI FHIR terminology
/// server as a CodeSystem resource. Unlike ICD-10/CVX/NDC/HCPCS/UCUM, LOINC requires a real account —
/// this reuses the existing, already-credentialed <see cref="ILoincReleaseClient"/> (same LOINC
/// username/password, stored as ProvisionedSecrets, that <c>LoincSynchronizationService</c> already
/// uses for the local-database sync path) rather than re-implementing LOINC authentication.
///
/// Parses LoincTable/Loinc.csv the same way LoincSynchronizationService does: code = LOINC_NUM,
/// display = LONG_COMMON_NAME (falling back to SHORTNAME), status = STATUS ("DEPRECATED" excluded).
/// </summary>
public sealed class HapiLoincTerminologySyncService : IHapiLoincTerminologySyncService
{
    private const string SystemUrl = "http://loinc.org";
    private const string ResourceId = "loinc-full";

    private readonly ILoincReleaseClient _releaseClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiLoincTerminologySyncService> _logger;

    public HapiLoincTerminologySyncService(
        ILoincReleaseClient releaseClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiLoincTerminologySyncService> logger)
    {
        _releaseClient = releaseClient;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    public async Task<HapiLoincSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Shared setting across every vocabulary — see HapiIcd10TerminologySyncService's remarks.
        var configuredDefault = _configuration["Terminology:BaseUrl"] ?? "http://hapi-terminology:8080/fhir";
        var serverBaseUrl = (await _settings.GetStringAsync(
            "Terminology:BaseUrl", configuredDefault, cancellationToken)).TrimEnd('/');

        _logger.LogInformation("Checking the current official LOINC release.");
        var release = await _releaseClient.GetCurrentReleaseAsync(cancellationToken);

        _logger.LogInformation("Downloading LOINC release {Version} (credentialed, checksum-verified).", release.Version);
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, cancellationToken);

        var concepts = ParseLoincCsv(zipPath);
        _logger.LogInformation("Parsed {Total} LOINC codes from release {Version}.", concepts.Count, release.Version);

        // Bypasses IHttpClientFactory — see HapiIcd10TerminologySyncService's remarks on the
        // app-wide resilience default stacking with, rather than being replaced by, a named override.
        using var serverClient = new HttpClient
        {
            BaseAddress = new Uri(serverBaseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(15),
        };

        var resource = BuildCodeSystemResource(concepts, release.Version);
        var response = await serverClient.PutAsJsonAsync($"CodeSystem/{ResourceId}", resource, cancellationToken);
        response.EnsureSuccessStatusCode();

        stopwatch.Stop();
        _logger.LogInformation(
            "LOINC {Version} loaded into the terminology server: {Total} codes in {Elapsed}.",
            release.Version, concepts.Count, stopwatch.Elapsed);

        return new HapiLoincSyncResult(release.Version, concepts.Count, stopwatch.Elapsed);
    }

    private static IReadOnlyList<Concept> ParseLoincCsv(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("LoincTable/Loinc.csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("LoincTable/Loinc.csv was not found in the release archive.");

        using var stream = entry.Open();
        using var parser = new TextFieldParser(stream) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");

        var headers = parser.ReadFields() ?? throw new InvalidDataException("Loinc.csv has no header row.");
        var index = headers.Select((name, i) => (name, i)).ToDictionary(x => x.name.Trim(), x => x.i, StringComparer.OrdinalIgnoreCase);

        string Get(string[] row, params string[] fields)
        {
            foreach (var field in fields)
            {
                if (index.TryGetValue(field, out var i) && i < row.Length)
                {
                    return row[i]?.Trim() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        var byCode = new Dictionary<string, string>(120_000, StringComparer.Ordinal);
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields();
            if (row is null)
            {
                continue;
            }

            var code = Get(row, "LOINC_NUM");
            var status = Get(row, "STATUS");
            var display = Get(row, "LONG_COMMON_NAME", "SHORTNAME");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(display)
                || string.Equals(status, "DEPRECATED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byCode.TryAdd(code, display);
        }

        return byCode.Select(kv => new Concept(kv.Key, kv.Value)).ToList();
    }

    private static object BuildCodeSystemResource(IReadOnlyList<Concept> concepts, string version)
    {
        return new
        {
            resourceType = "CodeSystem",
            id = ResourceId,
            url = SystemUrl,
            version,
            name = "LOINC",
            title = $"LOINC {version} (auto-synced, credentialed)",
            status = "active",
            content = "complete",
            count = concepts.Count,
            concept = concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }

    private sealed record Concept(string Code, string Display);
}
