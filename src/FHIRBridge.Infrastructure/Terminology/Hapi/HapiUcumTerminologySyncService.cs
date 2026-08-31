using System.Net.Http.Json;
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
    private const string ResourceId = "ucum-full";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiUcumTerminologySyncService> _logger;
    private readonly HapiTerminologyServerClient _serverClient;

    public HapiUcumTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiUcumTerminologySyncService> logger,
        HapiTerminologyServerClient serverClient)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _serverClient = serverClient;
    }

    public async Task<HapiUcumSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Downloading official UCUM specification (ucum-essence.xml).");
        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiUcumTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        var concepts = await DownloadAndParseAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Parsed {Total} UCUM unit codes from the official specification.", concepts.Count);
        var resource = BuildCodeSystemResource(concepts);
        await _serverClient.PutCodeSystemAsync(ResourceId, resource, concepts.Count, TimeSpan.FromMinutes(5), cancellationToken);

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

        var results = new List<Concept>(350);
        foreach (var element in document.Root!.Elements(ns + "base-unit").Concat(document.Root.Elements(ns + "unit")))
        {
            var code = element.Attribute("Code")?.Value?.Trim();
            var display = element.Element(ns + "name")?.Value?.Trim();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(display))
            {
                continue;
            }

            results.Add(new Concept(code, display));
        }

        return results;
    }

    private static object BuildCodeSystemResource(IReadOnlyList<Concept> concepts)
    {
        return new
        {
            resourceType = "CodeSystem",
            id = ResourceId,
            url = SystemUrl,
            name = "UCUM",
            title = "UCUM (Unified Code for Units of Measure) (auto-synced from ucum.org)",
            status = "active",
            content = "complete",
            count = concepts.Count,
            concept = concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }

    private sealed record Concept(string Code, string Display);
}
