using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;

/// <summary>
/// eClinicalWorks write channel. eCW's contracted Create APIs take every write as a FHIR <b>transaction Bundle POSTed to
/// the base URL</b> (not a POST to <c>/{type}</c>), and answer with a <c>transaction-response</c> Bundle whose
/// <c>entry[0].response.status</c> is an eCW code ("1" success, else an error code such as 202 "patient already
/// exists") and whose <c>location</c> carries the new id. Searches, the granted scope and everything else go through
/// the plain FHIR channel.
/// </summary>
public sealed class EcwEhrWriteChannel : IEhrWriteChannel
{
    private const string SnomedSystem = "http://snomed.info/sct";
    private const string TelephoneEncounterCode = "185317003";

    private readonly FhirClientEhrWriteChannel _fhir;

    public EcwEhrWriteChannel(FhirClientEhrWriteChannel fhir)
    {
        _fhir = fhir;
    }

    public Guid TargetConnectionId => _fhir.TargetConnectionId;

    public SourceSystemType TargetVendor => _fhir.TargetVendor;

    public string TargetBaseUrl => _fhir.TargetBaseUrl;

    public Guid? DestinationId => _fhir.DestinationId;

    public EhrWriteBackRunOptions Options => _fhir.Options;

    public bool VendorWriteApisActivated => _fhir.VendorWriteApisActivated;

    public Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken) => _fhir.GetGrantedScopeAsync(cancellationToken);

    public Task<EhrSearchOutcome> SearchByIdentifierAsync(string resourceType, string system, string value, CancellationToken cancellationToken) =>
        _fhir.SearchByIdentifierAsync(resourceType, system, value, cancellationToken);

    public Task<EhrSearchOutcome> SearchForPatientAsync(string resourceType, string targetPatientId, CancellationToken cancellationToken) =>
        _fhir.SearchForPatientAsync(resourceType, targetPatientId, cancellationToken);

    public Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken) =>
        _fhir.MatchPatientAsync(patientJson, cancellationToken);

    public async Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken)
    {
        string bundle;
        try
        {
            bundle = BuildTransaction(resourceJson);
        }
        catch (JsonException)
        {
            return new EhrCreateOutcome(EhrCreateKind.Rejected, null, null, [new EhrOutcomeIssue("error", "invalid", "not-a-fhir-resource", null)]);
        }

        var result = await _fhir.Client.SendAsync(
            new FhirRawWriteRequest("POST", _fhir.TargetBaseUrl.TrimEnd('/'), bundle, "application/fhir+json"),
            _fhir.Source,
            cancellationToken);
        return Interpret(result);
    }

    /// <summary>An open telephone encounter for history items to be filed on, as eCW's Encounter (Telephone
    /// Encounters) API defines one: type SNOMED 185317003, status planned. The class code follows eCW's own sample.</summary>
    public Task<EhrCreateOutcome> CreateHolderEncounterAsync(string targetPatientId, CancellationToken cancellationToken)
    {
        var encounter = new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = "planned",
            ["class"] = new JsonObject { ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ActCode", ["code"] = "VR" },
            ["type"] = new JsonArray(new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = SnomedSystem, ["code"] = TelephoneEncounterCode, ["display"] = "Telephone encounter" }),
            }),
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{targetPatientId}" },
            ["reasonCode"] = new JsonArray(new JsonObject { ["text"] = "History imported from another EHR" }),
        };
        return CreateAsync("Encounter", encounter.ToJsonString(), cancellationToken);
    }

    /// <summary>One POST entry, typed by the resource itself: a MedicationRequest shaped for eCW is a
    /// MedicationStatement.</summary>
    internal static string BuildTransaction(string resourceJson)
    {
        var resource = JsonNode.Parse(resourceJson) as JsonObject ?? throw new JsonException("Not a JSON object.");
        var type = resource["resourceType"]?.GetValue<string>() ?? throw new JsonException("No resourceType.");
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
                ["resource"] = resource,
                ["request"] = new JsonObject { ["method"] = "POST", ["url"] = type },
            }),
        };
        return bundle.ToJsonString();
    }

    /// <summary>
    /// Reads eCW's transaction-response. An HTTP failure keeps the connector's verdict (an open outcome stays
    /// unknown) and adds eCW's own status code, which it sends in the Bundle on errors too. A 2xx with status "1" (or an
    /// HTTP-style "201 Created") and a location is created; a 2xx with any other status is a refusal with that code; a 2xx
    /// eCW did not explain is unknown, never assumed to have failed.
    /// </summary>
    internal static EhrCreateOutcome Interpret(FhirRawWriteResult result)
    {
        var (status, location) = ReadFirstResponse(result.Body);
        var issues = result.Issues
            .Select(i => new EhrOutcomeIssue(i.Severity, i.Code, i.DetailCodes.FirstOrDefault(), i.Expressions.FirstOrDefault()))
            .ToList();
        var ecwCode = status is not null && !IsSuccessStatus(status) ? FirstToken(status) : null;
        if (ecwCode is not null)
        {
            issues.Insert(0, new EhrOutcomeIssue("error", "processing", ecwCode, null));
        }

        if (result.Kind != FhirWriteOutcomeKind.Created)
        {
            var kind = result.Kind == FhirWriteOutcomeKind.Rejected ? EhrCreateKind.Rejected : EhrCreateKind.Unknown;
            return new EhrCreateOutcome(kind, result.StatusCode, null, issues);
        }

        if (ecwCode is not null)
        {
            return new EhrCreateOutcome(EhrCreateKind.Rejected, result.StatusCode, null, issues);
        }

        var id = IdFromLocation(location);
        return id is null
            ? new EhrCreateOutcome(EhrCreateKind.Unknown, result.StatusCode, null, issues)
            : new EhrCreateOutcome(EhrCreateKind.Created, result.StatusCode, id, issues);
    }

    private static (string? Status, string? Location) ReadFirstResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            var response = (JsonNode.Parse(body)?["entry"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["response"] as JsonObject;
            return (AsString(response?["status"]), AsString(response?["location"]));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.ToJsonString()
            : null;

    /// <summary>eCW's success code is "1". Its error codes overlap HTTP's (201 "multiple patients found", 202 "patient
    /// already exists"), so a bare 2xx number is an eCW error; only an HTTP-style status with its reason phrase
    /// ("201 Created") counts as success.</summary>
    private static bool IsSuccessStatus(string status)
    {
        var parts = status.Trim().Split(' ', 2);
        return parts[0] == "1"
            || (parts.Length == 2 && parts[0].Length == 3 && parts[0][0] == '2' && int.TryParse(parts[0], out _));
    }

    private static string FirstToken(string status) => status.Trim().Split(' ', 2)[0];

    /// <summary>The id from <c>Type/id</c> (eCW sends the type it filed, which may differ from the one sent).</summary>
    private static string? IdFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var segments = location.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var history = Array.IndexOf(segments, "_history");
        var end = history >= 0 ? history : segments.Length;
        return end >= 2 ? Uri.UnescapeDataString(segments[end - 1]) : null;
    }
}
