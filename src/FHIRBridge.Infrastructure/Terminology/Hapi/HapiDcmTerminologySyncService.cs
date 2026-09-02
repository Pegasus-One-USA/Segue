using System.Net;
using System.Xml.Linq;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official DICOM Controlled Terminology (DCM) OWL/RDF ontology and loads it into the
/// embedded HAPI FHIR terminology server as a CodeSystem resource. Mirrors
/// <see cref="HapiUcumTerminologySyncService"/>'s shape, with one real difference: NEMA only
/// publishes this file over anonymous FTP (ftp://medical.nema.org/...) — no HTTPS mirror exists
/// (confirmed by checking several plausible paths) — so this uses <see cref="FtpWebRequest"/>
/// instead of <see cref="HttpClient"/> for the download step specifically. <see cref="FtpWebRequest"/>
/// is marked obsolete in .NET but remains fully functional; there is no non-obsolete BCL alternative
/// for anonymous FTP, and adding a third-party FTP client dependency for one legacy source wasn't
/// judged worth it here.
///
/// Source: ftp://medical.nema.org/MEDICAL/Dicom/Resources/Ontology/DCM/dcm.owl — public, no
/// credentials (anonymous FTP). RDF/XML: each &lt;rdf:Description rdf:about="...DCM/{code}"&gt;
/// with a &lt;skos:notation&gt; (code) and &lt;skos:prefLabel&gt; (display); entries marked
/// &lt;owl:deprecated&gt;true&lt;/owl:deprecated&gt; are retired and excluded, same filtering
/// approach as LOINC's STATUS=DEPRECATED / SNOMED's inactive concepts.
/// </summary>
#pragma warning disable SYSLIB0014, SYSLIB0025 // FtpWebRequest/WebRequest.Create are obsolete; see remarks above.
public sealed class HapiDcmTerminologySyncService : IHapiDcmTerminologySyncService
{
    private const string FtpUrl = "ftp://medical.nema.org/MEDICAL/Dicom/Resources/Ontology/DCM/dcm.owl";
    private const string SystemUrl = "http://dicom.nema.org/resources/ontology/DCM";

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Skos = "http://www.w3.org/2004/02/skos/core#";
    private static readonly XNamespace Owl = "http://www.w3.org/2002/07/owl#";

    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiDcmTerminologySyncService> _logger;
    private readonly HapiLocalTerminologyWriter _localWriter;

    public HapiDcmTerminologySyncService(
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<HapiDcmTerminologySyncService> logger,
        HapiLocalTerminologyWriter localWriter)
    {
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
        _localWriter = localWriter;
    }

    public async Task<HapiDcmSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        _logger.LogInformation("Downloading official DICOM Controlled Terminology (DCM) ontology via FTP.");
        var concepts = await DownloadAndParseAsync(cancellationToken);
        _logger.LogInformation("Parsed {Total} active DCM concepts from the official ontology.", concepts.Count);
        await _localWriter.WriteConceptsAsync(
            SystemUrl, "DCM", version: null, concepts.Select(c => (c.Code, c.Display)), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "DCM loaded into the terminology server: {Total} codes in {Elapsed}.", concepts.Count, stopwatch.Elapsed);

        return new HapiDcmSyncResult(concepts.Count, stopwatch.Elapsed);
    }

    private static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(CancellationToken ct)
    {
        var request = (FtpWebRequest)WebRequest.Create(FtpUrl);
        request.Method = WebRequestMethods.Ftp.DownloadFile;
        request.UsePassive = true;
        request.UseBinary = true;
        request.Credentials = new NetworkCredential("anonymous", "anonymous@example.com");

        using var response = (FtpWebResponse)await request.GetResponseAsync().WaitAsync(ct);
        await using var responseStream = response.GetResponseStream();
        var document = await XDocument.LoadAsync(responseStream, LoadOptions.None, ct);

        var results = new List<Concept>(6_000);
        foreach (var description in document.Root!.Elements(Rdf + "Description"))
        {
            var deprecated = description.Element(Owl + "deprecated")?.Value == "true";
            if (deprecated)
            {
                continue;
            }

            var code = description.Element(Skos + "notation")?.Value;
            var display = description.Element(Skos + "prefLabel")?.Value;
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(display))
            {
                continue;
            }

            results.Add(new Concept(code, display));
        }

        return results;
    }

    private sealed record Concept(string Code, string Display);
}
#pragma warning restore SYSLIB0014, SYSLIB0025
