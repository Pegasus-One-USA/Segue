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
/// loading it into a DOM. Only DescriptorRecord/DescriptorUI + DescriptorName/String are read (the
/// preferred term) — qualifiers, concepts, scope notes, etc. are skipped for speed.
/// </summary>
public sealed class HapiMeshTerminologySyncService : IHapiMeshTerminologySyncService
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
        var fileUrl = await FindLatestDescriptorUrlAsync(downloadClient, cancellationToken);
        _logger.LogInformation("Downloading MeSH descriptor file from {FileUrl}.", fileUrl);

        var concepts = await DownloadAndParseAsync(downloadClient, fileUrl, cancellationToken);
        _logger.LogInformation("Parsed {Total} MeSH descriptor concepts from the official release.", concepts.Count);
        await _localWriter.WriteConceptsAsync(
            SystemUrl, "MeSH", version: null, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "MeSH loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiMeshSyncResult(concepts.Count, stopwatch.Elapsed);
    }

    private static async Task<string> FindLatestDescriptorUrlAsync(HttpClient http, CancellationToken ct)
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

        return ListingPageUrl + latest.FileName;
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

                if (code is not null && display is not null)
                {
                    break;
                }
            }

            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(display))
            {
                results.Add(new Concept(code, display));
            }
        }

        return results;
    }

    private sealed record Concept(string Code, string Display);
}
