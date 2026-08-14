using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes resources to a FHIR repository via REST <c>PUT [base]/{ResourceType}/{id}</c> (update-or-create).
/// When the mapped record carries the normalized source FHIR JSON (<see cref="MappedDestinationRecord.SourceJson"/>),
/// that valid FHIR resource is persisted as <c>application/fhir+json</c> — with its <c>id</c> reconciled to the URL so
/// the update contract holds on a validating server (e.g. HAPI). When no FHIR JSON is present (non-FHIR flows), it
/// falls back to posting the flattened mapped payload to a permissive ingestion endpoint.
/// </summary>
public sealed class MappedFhirRepositoryDestinationWriter : IConfiguredDestinationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedFhirRepositoryDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        // Resolve the FHIR base URL. The configured-pipeline path loads the destination from the DB with Target set;
        // the workflow-graph run path reconstructs the destination from node config where the base URL lives only in
        // the connection metadata bag (dest_fhirBaseUrl), so Target is null there — read metadata before falling back
        // to the (typically absent) secret. Mirrors MedplumConnectionMetadata.BaseUrl for the same graph-run reason.
        var baseUrl = (ResolveBaseUrl(destination)
            ?? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)).TrimEnd('/');
        var httpClient = _httpClientFactory.CreateClient(nameof(MappedFhirRepositoryDestinationWriter));

        // Isolate per-record failures instead of aborting the whole batch on the first non-2xx: a single resource a
        // validating server (e.g. HAPI) rejects — a bad reference, an unsupported element, a profile-validation error
        // — must not discard the hundreds of resources that would otherwise write cleanly. The failed records are
        // reported via DestinationWriteResult.RecordErrors, which the runtime executor surfaces as PartialSuccess
        // (mirrors MappedSqlServerDestinationWriter / MappedMedplumDestinationWriter).
        var written = 0;
        var recordErrors = new List<string>();
        var writtenIds = new List<string?>();

        // Write referenced resources before the resources that reference them: a server enforcing referential
        // integrity (e.g. HAPI with enforce_referential_integrity_on_write) rejects a Patient whose
        // generalPractitioner/managingOrganization points at a Practitioner/Organization it hasn't seen yet
        // (HAPI-1094). A stable ordinal sort by a coarse dependency tier (Organization/Location first, then
        // Practitioner et al., then Patient, then everything patient-scoped) puts targets ahead of their
        // referrers within this single batch. OrderBy is stable, so same-tier records keep source order.
        var ordered = records.OrderBy(DependencyTier).ToList();

        foreach (var record in ordered)
        {
            var (resourceType, resourceId, body) = BuildFhirResource(record);
            var endpoint = $"{baseUrl}/{resourceType}/{resourceId}";
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/fhir+json");
                using var response = await httpClient.PutAsync(endpoint, content, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    written++;
                    writtenIds.Add(record.SourceResourceId);
                }
                else
                {
                    var detail = await ReadResponseDetailAsync(response, cancellationToken);
                    recordErrors.Add($"{resourceType}/{resourceId}: HTTP {(int)response.StatusCode} {detail}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                recordErrors.Add($"{resourceType}/{resourceId}: {ex.Message}");
            }
        }

        return new DestinationWriteResult(
            written,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenIds);
    }

    // Coarse write-ordering tiers so a resource is written after the resources it typically references. Not a full
    // topological sort (FHIR references form cycles in general) — just enough to satisfy a referential-integrity
    // server for the common Patient-plus-supporting-resources export: Organizations/Locations first, then
    // Practitioners and other standalone catalog resources, then PractitionerRole (references both), then Patient
    // and RelatedPerson, then everything clinical (which references Patient). Unknown types fall in the last tier.
    private static readonly IReadOnlyDictionary<string, int> DependencyTiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Organization"] = 0,
        ["Location"] = 0,
        ["Endpoint"] = 0,
        ["HealthcareService"] = 1,
        ["Medication"] = 1,
        ["Substance"] = 1,
        ["Device"] = 1,
        ["Practitioner"] = 1,
        ["PractitionerRole"] = 2,
        ["Patient"] = 3,
        ["RelatedPerson"] = 3,
    };

    private static int DependencyTier(MappedDestinationRecord record)
    {
        var resourceType = record.ResourceType;
        if (string.IsNullOrWhiteSpace(resourceType)
            && !string.IsNullOrWhiteSpace(record.SourceJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(record.SourceJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("resourceType", out var element)
                    && element.ValueKind == JsonValueKind.String)
                {
                    resourceType = element.GetString();
                }
            }
            catch (JsonException)
            {
                // Fall through to the default tier.
            }
        }

        return resourceType is not null && DependencyTiers.TryGetValue(resourceType, out var tier) ? tier : 4;
    }

    /// <summary>Reads a failed response body (typically a FHIR <c>OperationOutcome</c>) as a short diagnostic string.</summary>
    private static async Task<string> ReadResponseDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return body.Length > 500 ? body[..500] + "…" : body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return response.ReasonPhrase ?? "(no response body)";
        }
    }

    /// <summary>
    /// The FHIR base URL: the destination's <see cref="DestinationConfiguration.Target"/> when set (configured-pipeline
    /// path), else the <c>dest_fhirBaseUrl</c>/<c>fhirBaseUrl</c> key on <see cref="DestinationConfiguration.ConnectionMetadataJson"/>
    /// (workflow-graph run path, where Target is reconstructed empty). Returns null when neither is present.
    /// </summary>
    private static string? ResolveBaseUrl(DestinationConfiguration destination)
    {
        if (!string.IsNullOrWhiteSpace(destination.Target))
        {
            return destination.Target;
        }

        if (string.IsNullOrWhiteSpace(destination.ConnectionMetadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(destination.ConnectionMetadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in new[] { "dest_fhirBaseUrl", "fhirBaseUrl" })
            {
                if (doc.RootElement.TryGetProperty(key, out var element)
                    && element.ValueKind == JsonValueKind.String
                    && element.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed metadata → treat as absent; the secret fallback / a clear downstream failure takes over.
        }

        return null;
    }

    /// <summary>
    /// Produces a (resourceType, id, body) triple to PUT. Prefers the normalized FHIR resource; reconciles its
    /// <c>id</c> to a stable value so URL and body agree. Falls back to the flattened payload for non-FHIR flows.
    /// </summary>
    private static (string ResourceType, string Id, string Body) BuildFhirResource(MappedDestinationRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SourceJson)
            && JsonNode.Parse(record.SourceJson) is JsonObject resource
            && resource["resourceType"]?.GetValue<string>() is { Length: > 0 } resourceType)
        {
            var id = resource["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                id = string.IsNullOrWhiteSpace(record.SourceResourceId)
                    ? Guid.NewGuid().ToString("N")
                    : record.SourceResourceId;
            }

            // Namespace a purely-numeric logical id so a server that reserves numeric ids for its own assignment
            // (HAPI's default client-id strategy: "clients may only assign IDs which contain at least one
            // non-numeric character", HAPI-0960) will accept the client-supplied PUT. The rewrite is a pure,
            // deterministic function of the id, and RewriteNumericReferences applies the identical transform to
            // every "Type/{numericId}" reference in the resource — so a Patient's managingOrganization still points
            // at the (also-namespaced) Organization that was written for it. Alphanumeric ids (most Patients) are
            // left untouched, so they upsert in place rather than forking a duplicate under a new id.
            id = SafenNumericId(id!);
            resource["id"] = id;
            RewriteNumericReferences(resource);

            return (resourceType, Uri.EscapeDataString(id), resource.ToJsonString(JsonOptions));
        }

        // Non-FHIR fallback: post the flattened mapped payload to a permissive ingestion endpoint.
        var fallbackId = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? Guid.NewGuid().ToString("N")
            : Uri.EscapeDataString(record.SourceResourceId);
        return (record.ResourceType, fallbackId, MappedDestinationSerialization.ToJson(record));
    }

    private const string NumericIdNamespacePrefix = "fb-";

    /// <summary>Prefixes a purely-numeric logical id so a client may PUT-create it on a numeric-id-reserving server; leaves any id already containing a non-digit unchanged.</summary>
    private static string SafenNumericId(string id) =>
        IsAllDigits(id) ? NumericIdNamespacePrefix + id : id;

    /// <summary>
    /// Recursively rewrites every relative <c>"reference": "Type/{numericId}"</c> in the resource to
    /// <c>"Type/fb-{numericId}"</c>, matching <see cref="SafenNumericId"/> applied to the referenced resource's own
    /// id. Contained (<c>#</c>), logical (<c>urn:</c>), and absolute (<c>scheme://</c>) references — and references
    /// whose id already contains a non-digit — are left untouched.
    /// </summary>
    private static void RewriteNumericReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Key == "reference"
                        && property.Value is JsonValue value
                        && value.TryGetValue<string>(out var reference)
                        && SafenReference(reference) is { } rewritten
                        && !string.Equals(rewritten, reference, StringComparison.Ordinal))
                    {
                        obj[property.Key] = rewritten;
                    }
                    else
                    {
                        RewriteNumericReferences(property.Value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RewriteNumericReferences(item);
                }

                break;
        }
    }

    /// <summary>Returns the namespaced form of a relative <c>Type/{numericId}</c> reference, or the reference unchanged when it isn't one.</summary>
    private static string SafenReference(string reference)
    {
        if (string.IsNullOrEmpty(reference)
            || reference[0] == '#'
            || reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("://", StringComparison.Ordinal))
        {
            return reference;
        }

        var slash = reference.IndexOf('/');
        if (slash <= 0 || slash == reference.Length - 1)
        {
            return reference;
        }

        var resourceType = reference[..slash];
        var rest = reference[(slash + 1)..];
        // A relative reference can carry a version: Type/id/_history/vid — namespace only the id segment.
        var idEnd = rest.IndexOf('/');
        var id = idEnd < 0 ? rest : rest[..idEnd];
        var tail = idEnd < 0 ? string.Empty : rest[idEnd..];

        return IsAllLetters(resourceType) && IsAllDigits(id)
            ? $"{resourceType}/{NumericIdNamespacePrefix}{id}{tail}"
            : reference;
    }

    private static bool IsAllDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllLetters(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsLetter(c))
            {
                return false;
            }
        }

        return true;
    }
}
