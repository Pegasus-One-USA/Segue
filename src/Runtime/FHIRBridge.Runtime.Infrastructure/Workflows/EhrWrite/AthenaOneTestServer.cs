using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;

/// <summary>
/// Stands in for athenaOne in a test run (<c>dest_testAsVendor</c> = Athenahealth) on a Generic FHIR test server. The
/// athenaOne channel runs unchanged and sends its exact REST calls (<c>/v1/{practiceid}/...</c>, with departments and
/// lookups already filled in) through this client, which answers them in process and files each write on the test
/// server, where it can be inspected:
/// <list type="bullet">
/// <item><c>POST patients</c> registers a FHIR Patient built from the form fields, and answers with its id as
/// <c>patientid</c>.</item>
/// <item>Every other write is stored as a FHIR <c>Basic</c> holding the method, path and every form field exactly as
/// sent (code system <see cref="RequestSystem"/>, code = the API, e.g. <c>problems</c>), and answered with that id in
/// every id field athena's write APIs return.</item>
/// <item><c>GET patients/{id}</c> reads the patient from the test server (department <see cref="TestDepartmentId"/>);
/// <c>GET chart/{id}/encounters</c> turns the test server's Encounters into athena's shape; an allergen or medication
/// lookup always finds exactly the name asked for, with an id derived from it.</item>
/// </list>
/// FHIR calls (identifier search, encounter search, QuestionnaireResponse) go to the test server as they are. What a
/// test therefore does not prove: athena's own validation of each call, and that its reference lists hold the names.
/// Patients are the test server's own, by their FHIR id (numeric, as athenaOne's are, or not).
/// </summary>
public sealed class AthenaOneTestServer : IFhirWriteClient
{
    public const string TestPracticeId = "1";
    public const string TestDepartmentId = "1";
    public const string RequestSystem = "urn:fhirbridge:athenaone-test";

    // Every id field the athenaOne write profiles read back (AthenaOneWriteRequest.IdField).
    private static readonly string[] IdFields = ["problemid", "labresultid", "medicationentryid", "vaccineids", "clinicaldocumentid"];

    private readonly IFhirWriteClient _server;
    private readonly string _apiBase;
    private readonly string _fhirBaseUrl;

    public AthenaOneTestServer(IFhirWriteClient server, string fhirBaseUrl, string practiceId)
    {
        _server = server;
        _fhirBaseUrl = fhirBaseUrl.TrimEnd('/');
        _apiBase = AthenaOneEhrWriteChannel.ApiBase(fhirBaseUrl, practiceId);
    }

    public Task<FhirWriteResult> CreateAsync(string resourceType, string resourceJson, bool returnRepresentation, FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _server.CreateAsync(resourceType, resourceJson, returnRepresentation, source, cancellationToken);

    public Task<FhirSearchPage> SearchByIdentifierAsync(string resourceType, string system, string value, FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _server.SearchByIdentifierAsync(resourceType, system, value, source, cancellationToken);

    public Task<FhirSearchPage> SearchForPatientAsync(string resourceType, string patientId, FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _server.SearchForPatientAsync(resourceType, patientId, source, cancellationToken);

    public Task<FhirPatientMatchResult> MatchPatientAsync(string patientJson, FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _server.MatchPatientAsync(patientJson, source, cancellationToken);

    public async Task<FhirRawWriteResult> SendAsync(FhirRawWriteRequest request, FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (!request.Url.StartsWith(_apiBase + "/", StringComparison.Ordinal))
        {
            return await _server.SendAsync(request, source, cancellationToken);
        }

        var relative = request.Url[(_apiBase.Length + 1)..];
        var query = relative.IndexOf('?');
        var path = query >= 0 ? relative[..query] : relative;
        var parameters = query >= 0 ? ParseQuery(relative[(query + 1)..]) : new Dictionary<string, string>();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fields = request.FormFields ?? new Dictionary<string, string>();

        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return segments switch
            {
                ["patients", var id] => await ReadPatientAsync(id, source, cancellationToken),
                ["chart", var id, "encounters"] => await EncountersAsync(id, source, cancellationToken),
                ["reference", "allergies"] => Lookup(parameters, "allergenname", "allergenid"),
                ["reference", "medications"] => Lookup(parameters, "medication", "medicationid"),
                _ => NotFound(),
            };
        }

        if (segments is ["patients"])
        {
            return await RegisterPatientAsync(fields, source, cancellationToken);
        }

        return await FileRequestAsync(request.Method.ToUpperInvariant(), path, segments, fields, request.Multipart, source, cancellationToken);
    }

    private async Task<FhirRawWriteResult> ReadPatientAsync(string id, FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        var read = await _server.SendAsync(
            new FhirRawWriteRequest("GET", $"{_fhirBaseUrl}/Patient/{Uri.EscapeDataString(id)}", Idempotent: true, AddSourceQueryParameters: false),
            source,
            cancellationToken);
        return read.Kind != FhirWriteOutcomeKind.Created
            ? read with { Body = null }
            : Ok(new JsonArray(new JsonObject { ["patientid"] = id, ["primarydepartmentid"] = TestDepartmentId }));
    }

    private async Task<FhirRawWriteResult> EncountersAsync(string patientId, FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        var page = await _server.SearchForPatientAsync("Encounter", patientId, source, cancellationToken);
        if (!page.Succeeded)
        {
            return new FhirRawWriteResult(FhirWriteOutcomeKind.Rejected, page.StatusCode, null, page.Issues);
        }

        var encounters = new JsonArray();
        foreach (var json in page.Resources)
        {
            if (JsonNode.Parse(json) is not JsonObject encounter || encounter["id"]?.GetValue<string>() is not { } id)
            {
                continue;
            }

            var status = encounter["status"]?.GetValue<string>() switch
            {
                "in-progress" or "arrived" or "triaged" or "onleave" => "OPEN",
                "finished" => "CLOSED",
                _ => "DELETED",
            };
            var athena = new JsonObject { ["encounterid"] = id, ["status"] = status };
            if ((encounter["period"] as JsonObject)?["start"]?.GetValue<string>() is { Length: >= 10 } start
                && DateTime.TryParseExact(start[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                athena["encounterdate"] = date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            }

            encounters.Add(athena);
        }

        return Ok(new JsonObject { ["encounters"] = encounters });
    }

    /// <summary>athena's reference lists are not on a test server: the name asked for is found, once, with a stable
    /// numeric id, so the call that carries it can be inspected.</summary>
    private static FhirRawWriteResult Lookup(IReadOnlyDictionary<string, string> parameters, string nameField, string idField)
    {
        var name = parameters.GetValueOrDefault("searchvalue") ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant()));
        var id = (BitConverter.ToUInt32(hash, 0) % 900000 + 100000).ToString(CultureInfo.InvariantCulture);
        return Ok(new JsonArray(new JsonObject { [nameField] = name.Trim(), [idField] = id }));
    }

    private async Task<FhirRawWriteResult> RegisterPatientAsync(IReadOnlyDictionary<string, string> fields, FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        var patient = new JsonObject { ["resourceType"] = "Patient" };
        if (fields.GetValueOrDefault("ssn") is { Length: > 0 } ssn)
        {
            patient["identifier"] = new JsonArray(new JsonObject { ["system"] = "http://hl7.org/fhir/sid/us-ssn", ["value"] = ssn });
        }

        var name = new JsonObject();
        if (fields.GetValueOrDefault("lastname") is { Length: > 0 } last)
        {
            name["family"] = last;
        }

        if (fields.GetValueOrDefault("firstname") is { Length: > 0 } first)
        {
            name["given"] = new JsonArray(first);
        }

        patient["name"] = new JsonArray(name);
        var telecom = new JsonArray();
        foreach (var (field, system, use) in new[] { ("homephone", "phone", "home"), ("mobilephone", "phone", "mobile"), ("workphone", "phone", "work"), ("email", "email", "home") })
        {
            if (fields.GetValueOrDefault(field) is { Length: > 0 } value)
            {
                telecom.Add(new JsonObject { ["system"] = system, ["value"] = value, ["use"] = use });
            }
        }

        if (telecom.Count > 0)
        {
            patient["telecom"] = telecom;
        }

        if (fields.GetValueOrDefault("sex") is { } sex)
        {
            patient["gender"] = sex switch { "M" => "male", "F" => "female", _ => "unknown" };
        }

        if (fields.GetValueOrDefault("dob") is { } dob
            && DateTime.TryParseExact(dob, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthDate))
        {
            patient["birthDate"] = birthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var address = new JsonObject();
        var lines = new[] { "address1", "address2" }.Select(fields.GetValueOrDefault).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count > 0)
        {
            address["line"] = new JsonArray(lines.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
        }

        foreach (var (field, property) in new[] { ("city", "city"), ("state", "state"), ("zip", "postalCode") })
        {
            if (fields.GetValueOrDefault(field) is { Length: > 0 } value)
            {
                address[property] = value;
            }
        }

        if (address.Count > 0)
        {
            patient["address"] = new JsonArray(address);
        }

        var created = await _server.CreateAsync("Patient", patient.ToJsonString(), returnRepresentation: false, source, cancellationToken);
        return created.Kind == FhirWriteOutcomeKind.Created && created.ResourceId is { } id
            ? Ok(new JsonArray(new JsonObject { ["patientid"] = id }), created.StatusCode)
            : new FhirRawWriteResult(created.Kind, created.StatusCode, null, created.Issues);
    }

    /// <summary>Files one athenaOne write as a Basic on the test server: what was called, and every field as sent.</summary>
    /// <summary>athenaOne path words in the patient position of <c>chart/...</c> that are not a patient.</summary>
    private static readonly HashSet<string> NonPatientChartSegments = new(StringComparer.Ordinal) { "encounter", "configuration" };

    private async Task<FhirRawWriteResult> FileRequestAsync(
        string method,
        string path,
        string[] segments,
        IReadOnlyDictionary<string, string> fields,
        bool multipart,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var api = segments.Length > 0 ? segments[^1] : "request";
        var basic = new JsonObject
        {
            ["resourceType"] = "Basic",
            ["extension"] = new JsonArray(
                [
                    Extension("method", method),
                    Extension("path", path),
                    Extension("multipart", multipart ? "true" : "false"),
                    .. fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject
                    {
                        ["url"] = $"{RequestSystem}:field",
                        ["extension"] = new JsonArray(Extension("name", f.Key, nested: true), Extension("value", f.Value, nested: true)),
                    }),
                ]),
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = RequestSystem, ["code"] = api, ["display"] = $"{method} /v1/{TestPracticeId}/{path}" }),
            },
        };

        // chart/{patientid}/..., patients/{patientid}/...: the patient the write was filed for (the test server's own
        // patient id, numeric or not). chart/encounter/{encounterid}/... and chart/configuration/... name no patient.
        if (segments is ["chart" or "patients", var patientId, ..]
            && !NonPatientChartSegments.Contains(patientId)
            && AthenaOneEhrWriteChannel.TestServerPatientId(patientId) is not null)
        {
            basic["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" };
        }

        var created = await _server.CreateAsync("Basic", basic.ToJsonString(), returnRepresentation: false, source, cancellationToken);
        if (created.Kind != FhirWriteOutcomeKind.Created || created.ResourceId is not { } id)
        {
            return new FhirRawWriteResult(created.Kind, created.StatusCode, null, created.Issues);
        }

        var body = new JsonObject { ["success"] = true };
        foreach (var field in IdFields)
        {
            body[field] = id;
        }

        return Ok(body, created.StatusCode);
    }

    private static JsonObject Extension(string name, string value, bool nested = false) =>
        new() { ["url"] = nested ? name : $"{RequestSystem}:{name}", ["valueString"] = value };

    private static FhirRawWriteResult Ok(JsonNode body, int? status = 200) =>
        new(FhirWriteOutcomeKind.Created, status ?? 200, body.ToJsonString(), []);

    private static FhirRawWriteResult NotFound() =>
        new(FhirWriteOutcomeKind.Rejected, 404, null, []);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(equals >= 0 ? pair[..equals] : pair);
            parameters[key] = equals >= 0 ? Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')) : string.Empty;
        }

        return parameters;
    }
}
