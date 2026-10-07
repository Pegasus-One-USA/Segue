using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.DTOs;
using R = FHIRBridge.Application.Abstractions.Destinations.AthenaOneWriteRequest;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;

/// <summary>
/// athenahealth write channel. Clinical writes go to the proprietary athenaOne REST API
/// (<c>https://{host}/v1/{practiceid}/...</c>, form-encoded or multipart), described by the athena write profiles as
/// <see cref="AthenaOneWriteRequest"/> JSON; QuestionnaireResponse, identifier search and the granted scope stay on
/// athena's FHIR API. One token serves both: the write connection requests <c>athena/service/Athenanet.MDP.*</c>
/// alongside its FHIR scopes.
///
/// <para>Per run it caches each patient's department and each reference lookup, so a batch of fifty problems for one
/// patient reads the department once.</para>
///
/// <para><b>PHI.</b> Response bodies are read for ids and success flags only. athena's error text can echo what was
/// sent, so it is never logged or stored: a refusal is reported as <c>athena-{status}</c>.</para>
/// </summary>
public sealed class AthenaOneEhrWriteChannel : IEhrWriteChannel
{
    /// <summary>The athenaOne scope a 2-legged client is granted for the proprietary API.</summary>
    public const string ProprietaryApiScope = "athena/service/Athenanet.MDP.*";

    private readonly FhirClientEhrWriteChannel _fhir;
    private readonly string _apiBase;
    private readonly Dictionary<string, string?> _departments = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, string Name), string?> _lookups = new();

    public AthenaOneEhrWriteChannel(FhirClientEhrWriteChannel fhir, string practiceId)
    {
        _fhir = fhir;
        _apiBase = ApiBase(fhir.TargetBaseUrl, practiceId);
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

    public Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken) =>
        _fhir.MatchPatientAsync(patientJson, cancellationToken);

    /// <summary>A patient's encounters come from athenaOne, whose encounter ids are the ones its vitals API takes, as
    /// minimal FHIR Encounters: OPEN and REVIEW are in progress (athena modifies only those), CLOSED is finished.</summary>
    public async Task<EhrSearchOutcome> SearchForPatientAsync(string resourceType, string targetPatientId, CancellationToken cancellationToken)
    {
        if (resourceType != "Encounter")
        {
            return await _fhir.SearchForPatientAsync(resourceType, targetPatientId, cancellationToken);
        }

        var patientId = AthenaPatientId(targetPatientId);
        var department = patientId is null ? null : await DepartmentAsync(patientId, cancellationToken);
        if (patientId is null || department is null)
        {
            return new EhrSearchOutcome(false, null, [], []);
        }

        var result = await GetAsync($"chart/{patientId}/encounters?departmentid={Uri.EscapeDataString(department)}&showallstatuses=true", cancellationToken);
        if (result.Kind != FhirWriteOutcomeKind.Created)
        {
            return new EhrSearchOutcome(false, result.StatusCode, [], []);
        }

        return new EhrSearchOutcome(true, result.StatusCode, ToFhirEncounters(result.Body), []);
    }

    public async Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(resourceJson) as JsonObject;
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || Text(request, "resourceType") != R.ResourceType)
        {
            // Not an athenaOne call: a FHIR create athena's FHIR API takes (QuestionnaireResponse).
            return await _fhir.CreateAsync(resourceType, resourceJson, cancellationToken);
        }

        var operation = Text(request, R.Operation) ?? "request";
        var patientId = AthenaPatientId(Text(request, R.Patient));
        var path = Text(request, R.Path) ?? string.Empty;
        if (path.Contains(R.PatientIdToken, StringComparison.Ordinal))
        {
            if (patientId is null)
            {
                return Refused("athena-patient-id-unrecognised");
            }

            path = path.Replace(R.PatientIdToken, patientId, StringComparison.Ordinal);
        }

        if (path.Contains(R.EncounterIdToken, StringComparison.Ordinal))
        {
            if (Text(request, R.Encounter) is not { Length: > 0 } encounterId)
            {
                return Refused("athena-encounter-missing");
            }

            path = path.Replace(R.EncounterIdToken, Uri.EscapeDataString(encounterId), StringComparison.Ordinal);
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in request[R.Fields] as JsonObject ?? new JsonObject())
        {
            if (value is JsonValue scalar)
            {
                fields[name] = scalar.TryGetValue<string>(out var text) ? text : scalar.ToJsonString();
            }
        }

        foreach (var (name, value) in request[R.JsonFields] as JsonObject ?? new JsonObject())
        {
            if (value is not null)
            {
                fields[name] = value.ToJsonString();
            }
        }

        if (request[R.Department] is JsonValue flag && flag.TryGetValue<bool>(out var needsDepartment) && needsDepartment)
        {
            var department = patientId is null ? Options.TargetDepartmentId : await DepartmentAsync(patientId, cancellationToken);
            if (department is null)
            {
                return Refused("athena-department-unknown");
            }

            fields["departmentid"] = department;
        }

        foreach (var lookup in (request[R.Lookups] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var kind = Text(lookup, R.LookupKind) ?? string.Empty;
            var name = Text(lookup, R.LookupName) ?? string.Empty;
            var token = Text(lookup, R.LookupToken) ?? string.Empty;
            var id = await LookupAsync(kind, name, cancellationToken);
            if (id is null)
            {
                return Refused($"athena-{kind}-not-found");
            }

            foreach (var key in fields.Keys.ToList())
            {
                fields[key] = fields[key].Replace(token, id, StringComparison.Ordinal);
            }
        }

        var multipart = request[R.Multipart] is JsonValue m && m.TryGetValue<bool>(out var isMultipart) && isMultipart;
        var result = await _fhir.Client.SendAsync(
            new FhirRawWriteRequest(
                Text(request, R.Method) ?? "POST",
                $"{_apiBase}/{path}",
                FormFields: fields,
                Multipart: multipart,
                Idempotent: false,
                AddSourceQueryParameters: false),
            _fhir.Source,
            cancellationToken);

        return Interpret(result, Text(request, R.IdField), operation, patientId, fields);
    }

    /// <summary>
    /// A 2xx is created unless the body says otherwise (<c>success</c> false, or an <c>error</c>): the id comes from
    /// the request's id field (a string, number or array, or an object inside a one-element array). An API that returns
    /// no id (allergies, vitals) gets a stable synthetic one, so the ledger still records the write. A 409 means athena
    /// holds the same data already.
    /// </summary>
    internal static EhrCreateOutcome Interpret(
        FhirRawWriteResult result,
        string? idField,
        string operation,
        string? patientId,
        IReadOnlyDictionary<string, string> fields)
    {
        if (result.Kind != FhirWriteOutcomeKind.Created)
        {
            var kind = result.Kind == FhirWriteOutcomeKind.Rejected ? EhrCreateKind.Rejected : EhrCreateKind.Unknown;
            var issues = result.StatusCode is { } status
                ? new List<EhrOutcomeIssue> { new("error", "processing", $"athena-{status}", null) }
                : result.Issues.Select(i => new EhrOutcomeIssue(i.Severity, i.Code, i.DetailCodes.FirstOrDefault(), null)).ToList();
            return new EhrCreateOutcome(kind, result.StatusCode, null, issues);
        }

        JsonNode? body;
        try
        {
            body = string.IsNullOrWhiteSpace(result.Body) ? null : JsonNode.Parse(result.Body);
        }
        catch (JsonException)
        {
            body = null;
        }

        var item = body is JsonArray array ? array.FirstOrDefault() as JsonObject : body as JsonObject;
        var failed = item is not null
            && ((item["success"] is JsonValue success && (success.TryGetValue<bool>(out var ok) ? !ok : string.Equals(success.ToString(), "false", StringComparison.OrdinalIgnoreCase)))
                || item["error"] is not null);
        if (failed)
        {
            return new EhrCreateOutcome(EhrCreateKind.Rejected, result.StatusCode, null, [new EhrOutcomeIssue("error", "processing", "athena-error", null)]);
        }

        var id = idField is null ? null : IdOf(item?[idField]);
        if (id is null && idField is not null)
        {
            // athena said it worked and gave no id: something was filed, but this cannot say what.
            return new EhrCreateOutcome(EhrCreateKind.Unknown, result.StatusCode, null, []);
        }

        id ??= SyntheticId(operation, patientId, fields);
        return new EhrCreateOutcome(EhrCreateKind.Created, result.StatusCode, id, []);
    }

    /// <summary>The patient id the athenaOne calls carry. On a test server (a test run) the patient is the server's own
    /// FHIR Patient, which <see cref="AthenaOneTestServer"/> reads by that id whatever its form: a server that also
    /// holds copied records has non-numeric ids too.</summary>
    private string? AthenaPatientId(string? targetPatientId) =>
        PatientId(targetPatientId) ?? (Options.IsTestRun ? TestServerPatientId(targetPatientId) : null);

    /// <summary>A FHIR id (letters, digits, '-' and '.', at most 64), without a leading <c>Patient/</c>.</summary>
    internal static string? TestServerPatientId(string? targetPatientId)
    {
        var id = targetPatientId?.Trim() ?? string.Empty;
        if (id.StartsWith("Patient/", StringComparison.Ordinal))
        {
            id = id["Patient/".Length..];
        }

        return id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? id : null;
    }

    /// <summary>athenaOne's patient id from a target patient id: athena's FHIR id <c>a-{practice}.E-{enterpriseid}</c>
    /// (the number after "E-"), or a bare athena id (a patient this run created).</summary>
    internal static string? PatientId(string? targetPatientId)
    {
        if (string.IsNullOrWhiteSpace(targetPatientId))
        {
            return null;
        }

        var id = targetPatientId.Trim();
        if (id.StartsWith("Patient/", StringComparison.Ordinal))
        {
            id = id["Patient/".Length..];
        }

        var marker = id.LastIndexOf(".E-", StringComparison.Ordinal);
        if (marker >= 0)
        {
            id = id[(marker + 3)..];
        }

        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary><c>https://{host}/v1/{practiceid}</c> from athena's FHIR base URL (<c>https://{host}/fhir/r4</c>).</summary>
    internal static string ApiBase(string fhirBaseUrl, string practiceId)
    {
        var uri = new Uri(fhirBaseUrl);
        var practice = practiceId.Trim();
        var dash = practice.LastIndexOf('-');
        if (!practice.All(char.IsAsciiDigit) && dash >= 0)
        {
            // A practice reference (Organization/a-1.Practice-195900) rather than the bare number.
            practice = practice[(dash + 1)..];
        }

        return $"{uri.GetLeftPart(UriPartial.Authority)}/v1/{Uri.EscapeDataString(practice)}";
    }

    /// <summary>athenaOne encounters as minimal FHIR Encounters (id, status, period.start), the shape the writer's
    /// encounter picker reads.</summary>
    internal static IReadOnlyList<string> ToFhirEncounters(string? body)
    {
        JsonNode? root;
        try
        {
            root = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        var encounters = root is JsonObject obj ? obj["encounters"] as JsonArray : root as JsonArray;
        var result = new List<string>();
        foreach (var encounter in encounters?.OfType<JsonObject>() ?? [])
        {
            if (IdOf(encounter["encounterid"]) is not { } id)
            {
                continue;
            }

            var status = (Text(encounter, "status") ?? string.Empty).ToUpperInvariant() switch
            {
                "OPEN" or "REVIEW" => "in-progress",
                "CLOSED" => "finished",
                _ => "cancelled",
            };
            var fhir = new JsonObject { ["resourceType"] = "Encounter", ["id"] = id, ["status"] = status };
            if (Text(encounter, "encounterdate") is { } date
                && DateTime.TryParseExact(date, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                fhir["period"] = new JsonObject { ["start"] = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            }

            result.Add(fhir.ToJsonString());
        }

        return result;
    }

    /// <summary>The department a patient's chart is written in: the destination's, else the patient's own primary
    /// department.</summary>
    private async Task<string?> DepartmentAsync(string patientId, CancellationToken cancellationToken)
    {
        if (Options.TargetDepartmentId is { Length: > 0 } configured)
        {
            return configured;
        }

        if (_departments.TryGetValue(patientId, out var cached))
        {
            return cached;
        }

        string? department = null;
        var result = await GetAsync($"patients/{patientId}", cancellationToken);
        if (result.Kind == FhirWriteOutcomeKind.Created)
        {
            try
            {
                var root = string.IsNullOrWhiteSpace(result.Body) ? null : JsonNode.Parse(result.Body);
                var patient = root is JsonArray array ? array.FirstOrDefault() as JsonObject : root as JsonObject;
                department = IdOf(patient?["primarydepartmentid"]) ?? IdOf(patient?["departmentid"]);
            }
            catch (JsonException)
            {
                department = null;
            }
        }

        _departments[patientId] = department;
        return department;
    }

    /// <summary>athena's id for an allergen or medication with exactly this name (case-insensitive), or null when none
    /// or several match: a near match would file a different drug.</summary>
    private async Task<string?> LookupAsync(string kind, string name, CancellationToken cancellationToken)
    {
        if (_lookups.TryGetValue((kind, name), out var cached))
        {
            return cached;
        }

        var (path, nameField, idField) = kind switch
        {
            R.AllergenLookup => ("reference/allergies", "allergenname", "allergenid"),
            R.MedicationLookup => ("reference/medications", "medication", "medicationid"),
            _ => (null, null, null),
        };

        string? id = null;
        if (path is not null && name.Trim().Length >= 2)
        {
            var result = await GetAsync($"{path}?searchvalue={Uri.EscapeDataString(name.Trim())}", cancellationToken);
            if (result.Kind == FhirWriteOutcomeKind.Created)
            {
                try
                {
                    var root = string.IsNullOrWhiteSpace(result.Body) ? null : JsonNode.Parse(result.Body);
                    var matches = (root as JsonArray ?? new JsonArray())
                        .OfType<JsonObject>()
                        .Where(o => string.Equals(Text(o, nameField!)?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                        .Select(o => IdOf(o[idField!]))
                        .OfType<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    id = matches.Count == 1 ? matches[0] : null;
                }
                catch (JsonException)
                {
                    id = null;
                }
            }
        }

        _lookups[(kind, name)] = id;
        return id;
    }

    private Task<FhirRawWriteResult> GetAsync(string relativePath, CancellationToken cancellationToken) =>
        _fhir.Client.SendAsync(
            new FhirRawWriteRequest("GET", $"{_apiBase}/{relativePath}", Idempotent: true, AddSourceQueryParameters: false),
            _fhir.Source,
            cancellationToken);

    /// <summary>A refusal made before anything was sent (no HTTP status), so the next run tries again.</summary>
    private static EhrCreateOutcome Refused(string code) =>
        new(EhrCreateKind.Rejected, null, null, [new EhrOutcomeIssue("error", "processing", code, null)]);

    private static string SyntheticId(string operation, string? patientId, IReadOnlyDictionary<string, string> fields)
    {
        var content = string.Join("&", fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
        return $"{operation}:{patientId}:{hash}";
    }

    private static string? IdOf(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 0 => text,
        JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
        JsonArray array => array.Select(IdOf).FirstOrDefault(id => id is not null),
        _ => null,
    };

    private static string? Text(JsonNode? node, string property) =>
        (node as JsonObject)?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
