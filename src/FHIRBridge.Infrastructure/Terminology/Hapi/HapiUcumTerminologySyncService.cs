using System.Xml.Linq;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official UCUM specification XML and loads its unit codes into the embedded HAPI
/// FHIR terminology server as a CodeSystem resource. Mirrors <see cref="HapiCvxTerminologySyncService"/>'s
/// shape; the source here is XML, not a zip or HTML table.
///
/// Source: https://raw.githubusercontent.com/ucum-org/ucum/main/ucum-essence.xml — the canonical
/// machine-readable UCUM spec published by Regenstrief/UCUM.org, no credentials required. Loads
/// &lt;base-unit&gt; and &lt;unit&gt; elements (the actual measurable-unit codes); &lt;prefix&gt;
/// elements are grammar (metric prefixes like "milli"/"kilo"), not standalone codes, and are
/// intentionally excluded. Confirmed reachable, ~24 prefixes / 7 base units / 305 units as of
/// 2026-08-25.
/// </summary>
public sealed class HapiUcumTerminologySyncService : IHapiUcumTerminologySyncService
{
    private const string SourceUrl = "https://raw.githubusercontent.com/ucum-org/ucum/main/ucum-essence.xml";
    private const string SystemUrl = "http://unitsofmeasure.org";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiUcumTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiUcumTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiUcumTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiUcumSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Downloading official UCUM specification (ucum-essence.xml).");
        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiUcumTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        var concepts = await DownloadAndParseAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Parsed {Total} UCUM unit codes from the official specification.", concepts.Count);
        await _localWriter.WriteConceptsAsync(
            SystemUrl, "UCUM", version: null, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "UCUM loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiUcumSyncResult(concepts.Count, stopwatch.Elapsed);
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, CancellationToken ct)
    {
        var xml = await http.GetStringAsync(SourceUrl, ct);
        var document = XDocument.Parse(xml);
        var ns = document.Root!.GetDefaultNamespace();

        // Keyed by code to dedupe: a handful of codes (e.g. "s") are defined as both a <base-unit> and
        // a <unit> in the same spec file — a CodeSystem can't contain duplicate concept codes (confirmed
        // via a real unique-index violation on "S" during a live sync), so first-seen wins, same
        // approach as NDC's/ICD-11's dedupe.
        var byCode = new Dictionary<string, string>(350, StringComparer.Ordinal);
        foreach (var element in document.Root!.Elements(ns + "base-unit").Concat(document.Root.Elements(ns + "unit")))
        {
            var code = element.Attribute("Code")?.Value?.Trim();
            var display = element.Element(ns + "name")?.Value?.Trim();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(display))
            {
                continue;
            }

            byCode.TryAdd(code, display);
        }

        return byCode.Select(kv => new Concept(kv.Key, kv.Value)).ToList();
    }

    private sealed record Concept(string Code, string Display);
}
