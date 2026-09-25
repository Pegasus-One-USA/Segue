using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official ICPC-3 dataset and loads it into the embedded HAPI FHIR terminology
/// server as a CodeSystem resource. Mirrors <see cref="HapiCvxTerminologySyncService"/>'s shape.
///
/// Source: https://ct.icpc-3.info/download.php?language=english — the same public JSON endpoint
/// the official "ICPC-3 Coding Tool" web app (ct.icpc-3.info) itself calls to populate its search
/// index; no authentication, confirmed reachable and returning real data. Each ICPC-3 code appears
/// as multiple rows (one per "rkind": preferred/description/inclusion/exclusion/snomed-CT/etc.). The
/// "preferred" row supplies the display, one per code, 1:1; the "description" row — present for 961 of
/// the 1,623 codes — is kept as the long description instead of being discarded. ICPC-3 publishes no
/// status or expiry signal of any kind, so every code is stored active.
/// </summary>
public sealed class HapiIcpc3TerminologySyncService : IHapiIcpc3TerminologySyncService
{
    private const string DataUrl = "https://ct.icpc-3.info/download.php?language=english";
    private const string SystemUrl = "http://terminology.hl7.org/CodeSystem/ICPC-3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiIcpc3TerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiIcpc3TerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiIcpc3TerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiIcpc3SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Downloading official ICPC-3 dataset.");
        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiIcpc3TerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        var concepts = await DownloadAndParseAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Parsed {Total} ICPC-3 concepts from the official dataset.", concepts.Count);
        await _localWriter.WriteConceptsAsync(
            SystemUrl,
            "ICPC3",
            version: null,
            concepts.Select(c => new TerminologyConceptRecord(
                c.Code,
                c.Display,
                LongDescription: c.LongDescription,
                IsActive: true)),
            cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "ICPC-3 loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiIcpc3SyncResult(concepts.Count, stopwatch.Elapsed);
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(DataUrl, ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<Concept>(2_000);
        // The "description" rows are a separate rkind from "preferred" and arrive interleaved with them,
        // so both are collected in one pass and joined by code afterwards.
        var descriptions = new Dictionary<string, string>(1_000, StringComparer.Ordinal);

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("rkind", out var rkind))
            {
                continue;
            }

            var kind = rkind.GetString();
            if (kind != "preferred" && kind != "description")
            {
                continue;
            }

            var code = row.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
            var rubric = row.TryGetProperty("rubric", out var rubricProp) ? rubricProp.GetString() : null;
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(rubric))
            {
                continue;
            }

            if (kind == "description")
            {
                descriptions.TryAdd(code, rubric);
                continue;
            }

            results.Add(new Concept(code, rubric));
        }

        foreach (var concept in results)
        {
            concept.LongDescription = descriptions.GetValueOrDefault(concept.Code);
        }

        return results;
    }

    private sealed record Concept(string Code, string Display)
    {
        /// <summary>Filled in after parsing, once the separate "description" rows have all been seen.</summary>
        public string? LongDescription { get; set; }
    }
}
