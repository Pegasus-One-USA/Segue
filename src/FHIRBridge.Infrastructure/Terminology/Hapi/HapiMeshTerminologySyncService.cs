using System.Text.RegularExpressions;
using System.Xml;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official NLM MeSH descriptor file and loads it into the embedded HAPI FHIR
/// terminology server as a CodeSystem resource. Mirrors <see cref="HapiHcpcsTerminologySyncService"/>'s
/// "find the latest dated file" pattern, since NLM publishes a new desc&lt;year&gt;.xml annually with
/// no stable permanent link.
///
/// Source: https://nlmpubs.nlm.nih.gov/projects/mesh/MESH_FILES/xmlmesh/ — public, no credentials;
/// NLM's Terms and Conditions explicitly offer these files "without any restrictions" (linked open
/// data). The descriptor file is large (~220MB+), so this streams it with XmlReader rather than
/// loading it into a DOM. DescriptorRecord/DescriptorUI + DescriptorName/String (the preferred term)
/// supply the code and display, and ScopeNote — MeSH's own prose definition — is kept as the long
/// description; qualifiers and concept relations are still skipped.
/// </summary>
public sealed class HapiMeshTerminologySyncService : IHapiMeshTerminologySyncService, IHapiVersionCheckable
{
    private const string ListingPageUrl = "https://nlmpubs.nlm.nih.gov/projects/mesh/MESH_FILES/xmlmesh/";
    private const string SystemUrl = "https://www.nlm.nih.gov/mesh";

    private static readonly Regex DescFileLinkPattern = new(
        @"href=""(desc(\d{4})\.xml)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiMeshTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiMeshTerminologySyncService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiMeshTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiMeshSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        var downloadClient = _httpClientFactory.CreateClient(nameof(HapiMeshTerminologySyncService) + ".Download");
        downloadClient.Timeout = TimeSpan.FromMinutes(5);

        _logger.LogInformation("Finding the latest official NLM MeSH descriptor file.");
        var (fileUrl, year) = await FindLatestDescriptorAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Downloading MeSH descriptor file from {FileUrl}.", fileUrl);

        var concepts = await DownloadAndParseAsync(downloadClient, fileUrl, cancellationToken);
        _logger.LogInformation("Parsed {Total} MeSH descriptor concepts from the official release.", concepts.Count);
        await _localWriter.WriteConceptsAsync(
            SystemUrl,
            "MeSH",
            year,
            concepts.Select(c => new TerminologyConceptRecord(
                c.Code,
                c.Display,
                LongDescription: c.LongDescription,
                IsActive: true)),
            cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "MeSH loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiMeshSyncResult(concepts.Count, stopwatch.Elapsed, year);
    }

    /// <summary>The published year alone (NLM publishes one descriptor file per year, no finer-grained
    /// versioning) — same value used both to pick the download URL and as the stored version, so a
    /// later "is there a newer one" check is a direct, reliable comparison.</summary>
    public async Task<string?> GetLatestAvailableVersionAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(HapiMeshTerminologySyncService) + ".Download");
        var (_, year) = await FindLatestDescriptorAsync(client, cancellationToken);
        return year;
    }

    private static async Task<(string Url, string Year)> FindLatestDescriptorAsync(HttpClient http, CancellationToken ct)
    {
        var html = await http.GetStringAsync(ListingPageUrl, ct);
        var latest = DescFileLinkPattern.Matches(html)
            .Select(m => (FileName: m.Groups[1].Value, Year: int.Parse(m.Groups[2].Value)))
            .OrderByDescending(x => x.Year)
            .FirstOrDefault();

        if (latest.FileName is null)
        {
            throw new InvalidOperationException(
                $"Could not find a desc<year>.xml link on {ListingPageUrl}. The page layout may have changed.");
        }

        return (ListingPageUrl + latest.FileName, latest.Year.ToString());
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(HttpClient http, string fileUrl, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(fileUrl, ct);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            Async = true,
        });

        var results = new List<Concept>(30_000);
        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "DescriptorRecord")
            {
                continue;
            }

            using var record = reader.ReadSubtree();
            await record.MoveToContentAsync();

            string? code = null;
            string? display = null;
            string? scopeNote = null;
            while (await record.ReadAsync())
            {
                if (record.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (code is null && record.Name == "DescriptorUI")
                {
                    code = await record.ReadElementContentAsStringAsync();
                }
                else if (display is null && record.Name == "DescriptorName")
                {
                    if (record.ReadToDescendant("String"))
                    {
                        display = await record.ReadElementContentAsStringAsync();
                    }
                }
                else if (scopeNote is null && record.Name == "ScopeNote")
                {
                    scopeNote = await record.ReadElementContentAsStringAsync();
                }

                // Deliberately no early break once code+display are known: ScopeNote sits further down
                // the record (inside ConceptList), so stopping at the name would never reach it. The
                // subtree reader still bounds this to one DescriptorRecord, so the extra reads are cheap
                // relative to the ~220MB stream this already walks end to end.
                if (code is not null && display is not null && scopeNote is not null)
                {
                    break;
                }
            }

            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(display))
            {
                // MeSH publishes no status or expiry signal, so every descriptor is stored active.
                results.Add(new Concept(code, display, string.IsNullOrWhiteSpace(scopeNote) ? null : scopeNote.Trim()));
            }
        }

        return results;
    }

    private sealed record Concept(string Code, string Display, string? LongDescription);
}
