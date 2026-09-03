using System.Net;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CDC CVX vaccine code table and loads it into the embedded HAPI FHIR
/// terminology server as a CodeSystem resource. Mirrors <see cref="HapiIcd10TerminologySyncService"/>'s
/// shape; the source here is a public HTML table (not a zip), so the "download" step is a GET + a
/// small regex-based table parser instead of a zip/fixed-width parser.
///
/// Source: https://www2.cdc.gov/vaccines/iis/iisstandards/vaccines.asp?rpt=cvx — one HTML table,
/// columns: Short Description, Full Vaccine name, CVX Code, Vaccine Status, Last Updated Date, Notes.
/// No credentials required. Confirmed reachable and ~290 rows as of 2026-08-25.
/// </summary>
public sealed class HapiCvxTerminologySyncService : IHapiCvxTerminologySyncService
{
    private const string SourceUrl = "https://www2.cdc.gov/vaccines/iis/iisstandards/vaccines.asp?rpt=cvx";
    private const string SystemUrl = "http://hl7.org/fhir/sid/cvx";

    // Matches one table row and captures its raw inner HTML (non-greedy, single-line mode for '.').
    private static readonly Regex RowPattern = new(
        @"<tr style='font-size:14px;height=15'>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);

    // Matches one <td>, ignoring its attributes, capturing inner content.
    private static readonly Regex CellPattern = new(
        @"<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiCvxTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiCvxTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiCvxTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiCvxSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("Downloading official CVX vaccine code table from CDC.");
        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiCvxTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        var concepts = await DownloadAndParseAsync(downloadClient, cancellationToken);
        var activeCount = concepts.Count(c => c.Active);
        _logger.LogInformation(
            "Parsed {Total} CVX codes ({Active} active) from the official table.", concepts.Count, activeCount);
        await _localWriter.WriteConceptsAsync(
            SystemUrl, "CVX", version: null, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "CVX loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiCvxSyncResult(concepts.Count, activeCount, stopwatch.Elapsed);
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, CancellationToken ct)
    {
        var html = await http.GetStringAsync(SourceUrl, ct);
        var results = new List<Concept>(400);

        foreach (Match row in RowPattern.Matches(html))
        {
            var cells = CellPattern.Matches(row.Groups[1].Value)
                .Select(m => WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", string.Empty)).Trim())
                .ToArray();

            // Columns: 0=Short Description, 1=Full Vaccine name, 2=CVX Code, 3=Status, 4=Last Updated, 5=Notes.
            if (cells.Length < 4)
            {
                continue;
            }

            var code = cells[2].Trim();
            var display = cells[1].Trim();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(display))
            {
                continue;
            }

            var active = string.Equals(cells[3].Trim(), "Active", StringComparison.OrdinalIgnoreCase);
            results.Add(new Concept(code, display, active));
        }

        return results;
    }

    private sealed record Concept(string Code, string Display, bool Active);
}
