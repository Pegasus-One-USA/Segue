using System.IO.Compression;
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
public sealed class HapiLoincTerminologySyncService : IHapiLoincTerminologySyncService, IHapiVersionCheckable
{
    private const string SystemUrl = "http://loinc.org";

    private readonly ILoincReleaseClient _releaseClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiLoincTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiLoincTerminologySyncService(
        ILoincReleaseClient releaseClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiLoincTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _releaseClient = releaseClient;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiLoincSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Checking the current official LOINC release.");
        var release = await _releaseClient.GetCurrentReleaseAsync(cancellationToken);

        _logger.LogInformation("Downloading LOINC release {Version} (credentialed, checksum-verified).", release.Version);
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, cancellationToken);

        var concepts = ParseLoincCsv(zipPath);
        _logger.LogInformation("Parsed {Total} LOINC codes from release {Version}.", concepts.Count, release.Version);
        await _localWriter.WriteConceptsAsync(
            SystemUrl,
            "LOINC",
            release.Version,
            concepts.Select(c => new TerminologyConceptRecord(
                c.Code,
                c.Display,
                ShortDescription: c.ShortDescription,
                LongDescription: c.LongDescription,
                LongCommonName: c.LongCommonName,
                IsActive: c.IsActive)),
            cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "LOINC {Version} loaded into the terminology server: {Total} codes in {Elapsed}.",
            release.Version, concepts.Count, stopwatch.Elapsed);

        return new HapiLoincSyncResult(release.Version, concepts.Count, stopwatch.Elapsed);
    }

    /// <summary>Exact match: the same field this service passes to WriteConceptsAsync as the stored
    /// version — LOINC's Download API reports it directly, no downstream re-extraction needed.</summary>
    public async Task<string?> GetLatestAvailableVersionAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetCurrentReleaseAsync(cancellationToken);
        return release.Version;
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

        var byCode = new Dictionary<string, Concept>(120_000, StringComparer.Ordinal);
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

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(display))
            {
                continue;
            }

            // DEPRECATED codes are now RETAINED as inactive rather than dropped outright: a historical
            // resource can legitimately carry one, and silently having no row at all makes that look
            // like an unknown code instead of a known-retired one. Of LOINC 2.83's four STATUS values
            // only DEPRECATED is treated as inactive — TRIAL and DISCOURAGED codes are still in use.
            var isActive = !string.Equals(status, "DEPRECATED", StringComparison.OrdinalIgnoreCase);
            var longCommonName = Get(row, "LONG_COMMON_NAME");
            var shortName = Get(row, "SHORTNAME");

            byCode.TryAdd(code, new Concept(
                code,
                display,
                string.IsNullOrWhiteSpace(shortName) ? null : shortName,
                // LOINC has no single "long description" column; DefinitionDescription is the nearest
                // real prose definition and is populated on ~15.7k of the 112k codes.
                Get(row, "DefinitionDescription") is { Length: > 0 } definition ? definition : null,
                string.IsNullOrWhiteSpace(longCommonName) ? null : longCommonName,
                isActive));
        }

        return byCode.Values.ToList();
    }

    private sealed record Concept(
        string Code,
        string Display,
        string? ShortDescription,
        string? LongDescription,
        string? LongCommonName,
        bool IsActive);
}
