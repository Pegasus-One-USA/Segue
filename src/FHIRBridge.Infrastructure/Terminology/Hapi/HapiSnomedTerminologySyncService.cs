using System.IO.Compression;
using System.Net.Http.Json;
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
public sealed class HapiSnomedTerminologySyncService : IHapiSnomedTerminologySyncService
{
    private const string ReleaseType = "snomed-ct-us-edition";
    private const string SystemUrl = "http://snomed.info/sct";
    private const string ResourceId = "snomed-full";
    private const string FullySpecifiedNameTypeId = "900000000000003001";
    private const string SynonymTypeId = "900000000000013009";

    private readonly IUtsReleaseClient _releaseClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiSnomedTerminologySyncService> _logger;

    public HapiSnomedTerminologySyncService(
        IUtsReleaseClient releaseClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiSnomedTerminologySyncService> logger)
    {
        _releaseClient = releaseClient;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    public async Task<HapiSnomedSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Shared setting across every vocabulary — see HapiIcd10TerminologySyncService's remarks.
        var configuredDefault = _configuration["Terminology:BaseUrl"] ?? "http://hapi-terminology:8080/fhir";
        var serverBaseUrl = (await _settings.GetStringAsync(
            "Terminology:BaseUrl", configuredDefault, cancellationToken)).TrimEnd('/');

        _logger.LogInformation("Checking the current official SNOMED CT (US Edition) release.");
        var release = await _releaseClient.GetCurrentReleaseAsync(ReleaseType, cancellationToken);

        _logger.LogInformation("Downloading SNOMED CT release {Version} (credentialed).", release.ReleaseName ?? "unknown");
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, "Snomed", cancellationToken);

        var (version, concepts) = ParseSnomedRf2(zipPath);
        _logger.LogInformation("Parsed {Total} active SNOMED CT concepts from release {Version}.", concepts.Count, version);

        // Bypasses IHttpClientFactory — see HapiIcd10TerminologySyncService's remarks on the
        // app-wide resilience default stacking with, rather than being replaced by, a named override.
        using var serverClient = new HttpClient
        {
            BaseAddress = new Uri(serverBaseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(15),
        };

        var resource = BuildCodeSystemResource(concepts, version);
        var response = await serverClient.PutAsJsonAsync($"CodeSystem/{ResourceId}", resource, cancellationToken);
        response.EnsureSuccessStatusCode();

        stopwatch.Stop();
        _logger.LogInformation(
            "SNOMED CT loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiSnomedSyncResult(version, concepts.Count, stopwatch.Elapsed);
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

    private static object BuildCodeSystemResource(IReadOnlyList<Concept> concepts, string version)
    {
        return new
        {
            resourceType = "CodeSystem",
            id = ResourceId,
            url = SystemUrl,
            version,
            name = "SNOMEDCT",
            title = $"SNOMED CT US Edition {version} (auto-synced, credentialed)",
            status = "active",
            content = "complete",
            count = concepts.Count,
            concept = concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }

    private sealed record Concept(string Code, string Display);
}
