using System.IO.Compression;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official SNOMED CT (US Edition) release and loads it into the embedded HAPI
/// FHIR terminology server as a CodeSystem resource. Like RxNorm, SNOMED CT needs a real credential —
/// this reuses the existing, already-credentialed <see cref="IUtsReleaseClient"/> (same UMLS/UTS API
/// key that <c>SnomedSynchronizationService</c> already uses for the local-database RF2 import).
///
/// Parses the RF2 Snapshot files the same way <c>SnomedImportService</c> does: Concept Snapshot for
/// the code + active flag, Description Snapshot for display text (Fully Specified Name, falling back
/// to the preferred-term Synonym) — RF2 is plain tab-delimited with a header row, no quoting. Only
/// active concepts are loaded (inactive/retired concepts are excluded, same filtering rule as the
/// existing importer). This is a subset of the full RF2 model (no relationships/subsumption — a
/// plain code system, not the full graph) — sufficient for $lookup/$validate-code, not for ECL or
/// hierarchy queries.
/// </summary>
public sealed class HapiSnomedTerminologySyncService : IHapiSnomedTerminologySyncService, IHapiVersionCheckable
{
    private const string ReleaseType = "snomed-ct-us-edition";
    private const string SystemUrl = "http://snomed.info/sct";
    private const string FullySpecifiedNameTypeId = "900000000000003001";
    private const string SynonymTypeId = "900000000000013009";

    private readonly IUtsReleaseClient _releaseClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiSnomedTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiSnomedTerminologySyncService(
        IUtsReleaseClient releaseClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiSnomedTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _releaseClient = releaseClient;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiSnomedSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Checking the current official SNOMED CT (US Edition) release.");
        var release = await _releaseClient.GetCurrentReleaseAsync(ReleaseType, cancellationToken);

        _logger.LogInformation("Downloading SNOMED CT release {Version} (credentialed).", release.ReleaseName ?? "unknown");
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, "Snomed", cancellationToken);

        var (version, concepts) = ParseSnomedRf2(zipPath);
        _logger.LogInformation("Parsed {Total} active SNOMED CT concepts from release {Version}.", concepts.Count, version);
        await _localWriter.WriteConceptsAsync(
            SystemUrl, "SNOMEDCT", version, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "SNOMED CT loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiSnomedSyncResult(version, concepts.Count, stopwatch.Elapsed);
    }

    /// <summary>Best-effort: UTS's release-check endpoint reports a release date/name, not the exact
    /// 8-digit date this service actually stores as its version (extracted from the downloaded RF2
    /// file's own name — see <see cref="ExtractVersion"/>). Normalizing the release date to the same
    /// yyyyMMdd shape keeps the two directly comparable for the common case; falls back to the raw
    /// release name only if UTS didn't report a date at all.</summary>
    public async Task<string?> GetLatestAvailableVersionAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetCurrentReleaseAsync(ReleaseType, cancellationToken);
        return release.ReleaseDateUtc?.ToString("yyyyMMdd") ?? release.ReleaseName;
    }

    private static (string Version, IReadOnlyList<Concept> Concepts) ParseSnomedRf2(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var conceptEntry = FindEntry(archive, "_Concept_Snapshot")
            ?? throw new InvalidDataException("No Concept Snapshot file was found in the release archive.");
        var version = ExtractVersion(conceptEntry.Name);

        // Only FSN/preferred-term text per concept is kept in memory, not the full description rows —
        // same memory-bounding approach as the existing SnomedImportService.
        var fsnByConcept = new Dictionary<string, string>();
        var ptByConcept = new Dictionary<string, string>();
        var descriptionEntry = FindEntry(archive, "_Description_Snapshot");
        if (descriptionEntry is not null)
        {
            using var descStream = descriptionEntry.Open();
            using var descReader = new StreamReader(descStream);
            var descIndex = BuildIndex(descReader.ReadLine() ?? throw new InvalidDataException("RF2 file has no header."));
            string? descLine;
            while ((descLine = descReader.ReadLine()) is not null)
            {
                if (descLine.Length == 0)
                {
                    continue;
                }

                var row = descLine.Split('\t');
                if (Get(descIndex, row, "active") != "1")
                {
                    continue;
                }

                var conceptId = Get(descIndex, row, "conceptId");
                var typeId = Get(descIndex, row, "typeId");
                var term = Get(descIndex, row, "term");

                if (typeId == FullySpecifiedNameTypeId)
                {
                    fsnByConcept.TryAdd(conceptId, term);
                }
                else if (typeId == SynonymTypeId)
                {
                    ptByConcept.TryAdd(conceptId, term);
                }
            }
        }

        var concepts = new List<Concept>(400_000);
        using var conceptStream = conceptEntry.Open();
        using var conceptReader = new StreamReader(conceptStream);
        var conceptIndex = BuildIndex(conceptReader.ReadLine() ?? throw new InvalidDataException("RF2 file has no header."));
        string? conceptLine;
        while ((conceptLine = conceptReader.ReadLine()) is not null)
        {
            if (conceptLine.Length == 0)
            {
                continue;
            }

            var row = conceptLine.Split('\t');
            var id = Get(conceptIndex, row, "id");
            if (string.IsNullOrWhiteSpace(id) || Get(conceptIndex, row, "active") != "1")
            {
                continue;
            }

            var display = ptByConcept.TryGetValue(id, out var pt) ? pt : fsnByConcept.GetValueOrDefault(id);
            if (string.IsNullOrEmpty(display))
            {
                continue;
            }

            concepts.Add(new Concept(id, display));
        }

        if (concepts.Count == 0)
        {
            throw new InvalidDataException("The release contains no active SNOMED CT concepts.");
        }

        return (version, concepts);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string mustContain) =>
        archive.Entries.FirstOrDefault(x =>
            x.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && x.FullName.Contains(mustContain, StringComparison.OrdinalIgnoreCase));

    private static string ExtractVersion(string fileName)
    {
        var match = Regex.Match(fileName, @"(\d{8})");
        if (!match.Success)
        {
            throw new InvalidDataException($"Could not determine the release version from the RF2 file name {fileName}.");
        }

        return match.Groups[1].Value;
    }

    private static Dictionary<string, int> BuildIndex(string headerLine) =>
        headerLine.Split('\t').Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

    private static string Get(Dictionary<string, int> index, string[] row, string field) =>
        index.TryGetValue(field, out var i) && i < row.Length ? row[i] : string.Empty;

    private sealed record Concept(string Code, string Display);
}
