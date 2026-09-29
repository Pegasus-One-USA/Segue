using System.IO.Compression;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official RxNorm "Full Monthly Release" and loads it into the embedded HAPI
/// FHIR terminology server as a CodeSystem resource. Like LOINC, RxNorm needs a real credential —
/// this reuses the existing, already-credentialed <see cref="IUtsReleaseClient"/> (same UMLS/UTS
/// API key that <c>RxNormSynchronizationService</c> already uses for the local-database sync path)
/// rather than re-implementing UTS authentication.
///
/// Parses RXNCONSO.RRF the same way <c>RxNormImportService</c> does: pipe-delimited, no header,
/// fixed column order (RXCUI(0)...STR(14)...SUPPRESS(16)). RXNCONSO has many rows per RXCUI (one per
/// source vocabulary/term-type) — keeps the best candidate row per RXCUI (RXNORM-sourced wins, then
/// ISPREF='Y' wins), same tie-break logic as the existing importer, so display names match exactly.
/// </summary>
public sealed class HapiRxNormTerminologySyncService : IHapiRxNormTerminologySyncService, IHapiVersionCheckable
{
    private const string ReleaseType = "rxnorm-full-monthly-release";
    private const string SystemUrl = "http://www.nlm.nih.gov/research/umls/rxnorm";

    private readonly IUtsReleaseClient _releaseClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiRxNormTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiRxNormTerminologySyncService(
        IUtsReleaseClient releaseClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiRxNormTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _releaseClient = releaseClient;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiRxNormSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Checking the current official RxNorm release.");
        var release = await _releaseClient.GetCurrentReleaseAsync(ReleaseType, cancellationToken);

        _logger.LogInformation("Downloading RxNorm release {Version} (credentialed).", release.ReleaseName ?? "unknown");
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, "RxNorm", cancellationToken);

        var concepts = ParseRxnConso(zipPath);
        _logger.LogInformation("Parsed {Total} RxNorm concepts from release {Version}.", concepts.Count, release.ReleaseName);
        await _localWriter.WriteConceptsAsync(
            SystemUrl,
            "RxNorm",
            release.ReleaseName,
            concepts.Select(c => new TerminologyConceptRecord(
                c.Code,
                c.Display,
                LongCommonName: c.LongCommonName,
                IsActive: c.IsActive)),
            cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "RxNorm loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiRxNormSyncResult(release.ReleaseName, concepts.Count, stopwatch.Elapsed);
    }

    /// <summary>Exact match: this is the same field this service passes to WriteConceptsAsync as the
    /// stored version, so a comparison is fully reliable — no downstream re-extraction, unlike SNOMED.</summary>
    public async Task<string?> GetLatestAvailableVersionAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetCurrentReleaseAsync(ReleaseType, cancellationToken);
        return release.ReleaseName;
    }

    private static IReadOnlyList<Concept> ParseRxnConso(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("RXNCONSO.RRF", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("No RXNCONSO.RRF file was found in the release archive.");

        var candidates = new Dictionary<string, Candidate>(400_000, StringComparer.Ordinal);
        // Collected in the same single pass as the display candidates, since RXNCONSO is ~132MB and a
        // second read would double the parse cost for one extra column.
        var prescribableNames = new Dictionary<string, string>(40_000, StringComparer.Ordinal);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var row = line.Split('|');
            if (row.Length < 17)
            {
                continue;
            }

            var rxcui = row[0];
            if (string.IsNullOrWhiteSpace(rxcui))
            {
                continue;
            }

            var candidate = new Candidate(
                Name: row[14],
                IsRxNormSource: row[11].Equals("RXNORM", StringComparison.OrdinalIgnoreCase),
                IsPreferred: row[6].Equals("Y", StringComparison.OrdinalIgnoreCase),
                // SUPPRESS (col 16) is three-valued in the real release: N (812,754 rows), O (390,482,
                // obsolete) and E (7,222, non-human//editor-suppressed). Only N counts as in force.
                IsSuppressed: !row[16].Equals("N", StringComparison.OrdinalIgnoreCase),
                // TTY (col 12) = PSN is RxNorm's "prescribable name", a genuinely different string from
                // the SCD/IN/BN name on 30,434 RXCUIs (e.g. "Levo-T 112 MCG Oral Tablet" vs
                // "levothyroxine sodium 0.112 MG Oral Tablet [Levo-T]").
                TermType: row[12]);

            if (candidate.TermType.Equals("PSN", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(candidate.Name))
            {
                prescribableNames.TryAdd(rxcui, candidate.Name);
            }

            if (!candidates.TryGetValue(rxcui, out var existing) || IsBetterCandidate(candidate, existing))
            {
                candidates[rxcui] = candidate;
            }
        }

        return candidates
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Name))
            .Select(kv => new Concept(
                kv.Key,
                kv.Value.Name,
                prescribableNames.GetValueOrDefault(kv.Key),
                !kv.Value.IsSuppressed))
            .ToList();
    }

    private static bool IsBetterCandidate(Candidate candidate, Candidate existing)
    {
        if (candidate.IsRxNormSource != existing.IsRxNormSource)
        {
            return candidate.IsRxNormSource;
        }

        return candidate.IsPreferred && !existing.IsPreferred;
    }

    private sealed record Candidate(string Name, bool IsRxNormSource, bool IsPreferred, bool IsSuppressed, string TermType);

    private sealed record Concept(string Code, string Display, string? LongCommonName, bool IsActive);
}
