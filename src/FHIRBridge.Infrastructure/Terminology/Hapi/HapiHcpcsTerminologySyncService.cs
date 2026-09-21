using System.IO.Compression;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CMS HCPCS Level II quarterly release and loads it into the embedded
/// HAPI FHIR terminology server as a CodeSystem resource. Mirrors <see cref="HapiIcd10TerminologySyncService"/>'s
/// shape, with two differences from the simpler vocabularies:
///
/// 1. CMS publishes a new dated zip URL every quarter (e.g. "october-2026-alpha-numeric-hcpcs-file.zip")
///    with no stable permanent link, so this first scrapes the listing page for the most recent one.
///    The data file inside is likewise not stably named — a re-published quarter carries a revision
///    suffix after the ANWEB token (e.g. "HCPC2026_OCT_ANWEB_v2.txt"), so it is located by the ANWEB
///    token rather than an exact "_ANWEB.txt" suffix.
/// 2. The fixed-width "ANWEB" data file wraps long descriptions across multiple physical rows sharing
///    the same HCPCS code (a "sequence number" field, positions 6-10, increments by 100 per
///    continuation row) — reconstructing the full description requires concatenating consecutive
///    same-code rows' description chunks with a single space, per CMS's own documented record layout
///    (HCPC&lt;year&gt;_recordlayout.txt in the release zip).
///
/// Record layout (1-indexed): 1-5 HCPCS code; 6-10 sequence number; 12-91 long description chunk (80
/// chars, word-wrapped, no mid-word splits). Confirmed against the real October 2026 release.
/// </summary>
public sealed class HapiHcpcsTerminologySyncService : IHapiHcpcsTerminologySyncService, IHapiVersionCheckable
{
    private const string ListingPageUrl =
        "https://www.cms.gov/medicare/coding-billing/healthcare-common-procedure-system/quarterly-update";
    private const string SystemUrl = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";

    private static readonly Regex ZipLinkPattern = new(
        @"href=""(/files/zip/[a-z0-9\-]*alpha-numeric-hcpcs-file[a-z0-9\-]*\.zip)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiHcpcsTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiHcpcsTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiHcpcsTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiHcpcsSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiHcpcsTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(2);

        _logger.LogInformation("Finding the latest official CMS HCPCS Level II quarterly release.");
        var zipUrl = await FindLatestZipUrlAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Downloading HCPCS release from {ZipUrl}.", zipUrl);

        var concepts = await DownloadAndParseAsync(downloadClient, zipUrl, cancellationToken);
        _logger.LogInformation("Parsed {Total} HCPCS codes from the official release.", concepts.Count);
        var version = VersionFromZipUrl(zipUrl);
        await _localWriter.WriteConceptsAsync(
            SystemUrl, "HCPCS", version, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "HCPCS loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiHcpcsSyncResult(concepts.Count, stopwatch.Elapsed, version);
    }

    /// <summary>CMS's quarterly zip filename itself (e.g. "october-2026-alpha-numeric-hcpcs-file.zip")
    /// as the version identifier — there's no separately-published release number, but the filename
    /// changes every quarter and is the same value used both to check for updates and to store what
    /// was actually synced, so the comparison is reliable.</summary>
    public async Task<string?> GetLatestAvailableVersionAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(HapiHcpcsTerminologySyncService) + ".Download");
        var zipUrl = await FindLatestZipUrlAsync(client, cancellationToken);
        return VersionFromZipUrl(zipUrl);
    }

    private static string VersionFromZipUrl(string zipUrl) => zipUrl[(zipUrl.LastIndexOf('/') + 1)..];

    private static async Task<string> FindLatestZipUrlAsync(HttpClient http, CancellationToken ct)
    {
        var html = await http.GetStringAsync(ListingPageUrl, ct);
        var match = ZipLinkPattern.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not find an HCPCS release zip link on {ListingPageUrl}. The page layout may have changed.");
        }

        return "https://www.cms.gov" + match.Groups[1].Value;
    }

    /// <summary>
    /// Picks the ANWEB fixed-width data file out of a release zip's entry names.
    ///
    /// CMS does not name this file stably: when a quarter is re-published the ANWEB token carries a
    /// revision suffix (October 2026 ships "HCPC2026_OCT_ANWEB_v2.txt"), so matching on an exact
    /// "_ANWEB.txt" suffix silently finds nothing on precisely the corrected releases. Matching is
    /// therefore on the ANWEB token anywhere in the name, restricted to .txt so the zip's .xlsx copy
    /// of the same data and its ANWEB-named transaction report are excluded, and with the record
    /// layout document excluded. Where several revisions ship together the highest-sorting name wins,
    /// so a "_v2" supersedes a "_v1".
    /// </summary>
    internal static string? SelectDataFileName(IEnumerable<string> entryNames) =>
        entryNames
            .Where(n => n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                && n.Contains("ANWEB", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("recordlayout", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, string zipUrl, CancellationToken ct)
    {
        await using var zipBytes = await http.GetStreamAsync(zipUrl, ct);
        using var memory = new MemoryStream();
        await zipBytes.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = SelectDataFileName(archive.Entries.Select(e => e.Name)) is { } name
            ? archive.Entries.First(e => e.Name == name)
            : throw new InvalidOperationException("Could not find the ANWEB data file in the downloaded HCPCS release zip.");

        var results = new List<Concept>(20_000);
        string? currentCode = null;
        var currentChunks = new List<string>();

        void FlushCurrent()
        {
            if (currentCode is null || currentChunks.Count == 0)
            {
                return;
            }

            var display = string.Join(' ', currentChunks.Select(c => c.Trim()).Where(c => c.Length > 0));
            if (!string.IsNullOrEmpty(display))
            {
                results.Add(new Concept(currentCode, display));
            }
        }

        using var reader = new StreamReader(entry.Open());
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            // Continuation lines are NOT padded to the full 320-char record width — a line holding
            // only the tail of a wrapped description can be as short as ~12-40 chars. Only the code
            // field (0-5) needs to be reliably present; the description chunk is whatever remains
            // past position 11, however short.
            if (line.Length < 5)
            {
                continue;
            }

            var code = line.Substring(0, 5).Trim();
            var descriptionChunk = line.Length > 11 ? line.Substring(11, Math.Min(80, line.Length - 11)) : string.Empty;
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            if (code != currentCode)
            {
                FlushCurrent();
                currentCode = code;
                currentChunks = new List<string>();
            }

            currentChunks.Add(descriptionChunk);
        }

        FlushCurrent();
        return results;
    }

    private sealed record Concept(string Code, string Display);
}
