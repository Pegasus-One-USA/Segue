using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Destinations.Auth;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes resources to a FHIR repository via REST <c>PUT [base]/{ResourceType}/{id}</c> (update-or-create).
/// When the mapped record carries the normalized source FHIR JSON (<see cref="MappedDestinationRecord.SourceJson"/>),
/// that valid FHIR resource is persisted as <c>application/fhir+json</c> — with its <c>id</c> reconciled to the URL so
/// the update contract holds on a validating server (e.g. HAPI, Aidbox). When no FHIR JSON is present (non-FHIR
/// flows), it falls back to posting the flattened mapped payload to a permissive ingestion endpoint.
///
/// Authentication is opt-in via the non-secret <c>dest_fhirAuthType</c> metadata flag (absent/"none", "bearer", or
/// "clientCredentials" — see <see cref="FhirRepositoryAuthResolver"/>). When absent or "none" — every
/// <c>FhirRepository</c> row before this capability existed — base-URL resolution and the write loop are byte-for-
/// byte identical to before: no secret re-parsing, no header attached.
///
/// Writing is opt-in via the non-secret <c>dest_fhirWriteMode</c> metadata flag (absent/"individual" or "bundle").
/// Absent/"individual" — every row before this capability existed — keeps the exact one-<c>PUT</c>-per-record loop,
/// byte-for-byte. "bundle" instead sends every record with real FHIR JSON as one FHIR <c>Bundle</c>
/// (<c>type: "batch"</c>) POSTed once to the repository root, and reads the response Bundle's own per-entry
/// <c>response.status</c>/<c>outcome</c> to isolate one record's failure from the rest — a <c>batch</c> Bundle is
/// non-atomic by the FHIR spec, so this isolation falls out of the request shape itself rather than needing separate
/// per-record try/catch logic. Gated behind this flag (rather than made unconditional) because it changes the wire
/// format for every row that opts in, and Aidbox's real batch-response shape hasn't been live-verified — enable it
/// per-destination, confirm it behaves as the spec describes, before ever considering it as a new default. A record
/// with no FHIR JSON (the non-FHIR fallback flow) is structurally incompatible with a Bundle entry and always goes
/// out as its own individual request, in both modes, unchanged.
/// </summary>
public sealed class MappedFhirRepositoryDestinationWriter : IConfiguredDestinationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFhirDestinationTokenProvider _tokenProvider;

    public MappedFhirRepositoryDestinationWriter(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IFhirDestinationTokenProvider tokenProvider)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var authType = ConnectionMetadataReader.GetString(destination.ConnectionMetadataJson, "dest_fhirAuthType") ?? "none";

        string baseUrl;
        System.Net.Http.Headers.AuthenticationHeaderValue? authHeader;
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase))
        {
            // Exactly today's behavior — unchanged for every existing FhirRepository row.
            baseUrl = (destination.Target ?? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)).TrimEnd('/');
            authHeader = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(destination.Target))
            {
                throw new InvalidOperationException(
                    "FHIR repository destinations with dest_fhirAuthType other than 'none' must set Target to the FHIR " +
                    "base URL — the secret is reserved for auth credentials, not the URL.");
            }

            baseUrl = destination.Target.TrimEnd('/');
            authHeader = await FhirRepositoryAuthResolver.ResolveAsync(
                destination.ConnectionMetadataJson, destination.SecretReference, _secretProvider, _tokenProvider, cancellationToken);
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(MappedFhirRepositoryDestinationWriter));

        var useBundle = string.Equals(
            ConnectionMetadataReader.GetString(destination.ConnectionMetadataJson, "dest_fhirWriteMode"),
            "bundle",
            StringComparison.OrdinalIgnoreCase);

        if (!useBundle)
        {
            // Exactly today's behavior — byte-for-byte — when dest_fhirWriteMode is absent or anything other than
            // "bundle". One PUT per record; a failure throws and fails the whole route, as before.
            foreach (var record in records)
            {
                var (resourceType, resourceId, body) = BuildFhirResource(record);
                var endpoint = $"{baseUrl}/{resourceType}/{resourceId}";
                using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
                };
                if (authHeader is not null)
                {
                    request.Headers.Authorization = authHeader;
                }

                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
            }

            return new DestinationWriteResult(records.Count);
        }

        // Bundle mode: split into records with real FHIR JSON (bundleable) and the non-FHIR fallback flow
        // (structurally incompatible with a Bundle entry — always sent individually, same as today).
        var bundleEntries = new List<(MappedDestinationRecord Record, string ResourceType, JsonObject Resource)>();
        var fallbackRecords = new List<MappedDestinationRecord>();
        foreach (var record in records)
        {
            if (TryParseFhirResource(record, out var resourceType, out var resource))
            {
                bundleEntries.Add((record, resourceType, resource));
            }
            else
            {
                fallbackRecords.Add(record);
            }
        }

        var recordErrors = new List<string>();
        var writtenResourceIds = new List<string?>();

        if (bundleEntries.Count > 0)
        {
            await WriteBundleAsync(httpClient, baseUrl, authHeader, bundleEntries, recordErrors, writtenResourceIds, cancellationToken);
        }

        foreach (var record in fallbackRecords)
        {
            var (resourceType, resourceId, body) = BuildFhirResource(record);
            var endpoint = $"{baseUrl}/{resourceType}/{resourceId}";
            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
            };
            if (authHeader is not null)
            {
                request.Headers.Authorization = authHeader;
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            writtenResourceIds.Add(record.SourceResourceId);
        }

        return new DestinationWriteResult(
            writtenResourceIds.Count,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenResourceIds.Count > 0 || recordErrors.Count > 0 ? writtenResourceIds : null);
    }

    /// <summary>
    /// Builds one <c>Bundle</c> (<c>type: "batch"</c>) containing every entry, POSTs it once to the repository
    /// root, then correlates the response Bundle's entries back to <paramref name="entries"/> BY ARRAY INDEX — a
    /// batch Bundle preserves request order per the FHIR spec, and each entry's own <c>request.url</c> already pins
    /// the exact resource+id being written, so there's no need to parse an id back out of the response to know which
    /// record a given outcome belongs to. A transport-level failure (non-2xx on the outer POST) has no per-entry
    /// information to isolate against and fails the whole route, same as individual mode. A response/request entry
    /// count mismatch throws rather than risk zipping mismatched arrays and attributing the wrong error to the wrong
    /// record — a correctness risk, not a cosmetic one, in a healthcare pipeline.
    /// </summary>
    private static async Task WriteBundleAsync(
        HttpClient httpClient,
        string baseUrl,
        System.Net.Http.Headers.AuthenticationHeaderValue? authHeader,
        IReadOnlyList<(MappedDestinationRecord Record, string ResourceType, JsonObject Resource)> entries,
        List<string> recordErrors,
        List<string?> writtenResourceIds,
        CancellationToken cancellationToken)
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "batch",
            ["entry"] = new JsonArray(entries.Select(entry =>
            {
                var id = entry.Resource["id"]!.GetValue<string>();
                return (JsonNode)new JsonObject
                {
                    ["resource"] = entry.Resource,
                    ["request"] = new JsonObject
                    {
                        ["method"] = "PUT",
                        ["url"] = $"{entry.ResourceType}/{Uri.EscapeDataString(id)}"
                    }
                };
            }).ToArray())
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new StringContent(bundle.ToJsonString(JsonOptions), Encoding.UTF8, "application/fhir+json")
        };
        if (authHeader is not null)
        {
            request.Headers.Authorization = authHeader;
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"FHIR batch bundle POST to '{baseUrl}' failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var responseEntries = ParseBatchResponseEntries(responseBody);
        if (responseEntries.Count != entries.Count)
        {
            throw new InvalidOperationException(
                $"FHIR batch response entry count ({responseEntries.Count}) did not match request entry count " +
                $"({entries.Count}); cannot safely correlate results back to records.");
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var (record, resourceType, resource) = entries[i];
            var (isSuccess, diagnostics) = responseEntries[i];
            if (isSuccess)
            {
                writtenResourceIds.Add(record.SourceResourceId);
            }
            else
            {
                var id = resource["id"]?.GetValue<string>();
                var message = SafeErrorText.SanitizeOr(diagnostics, "FHIR destination rejected this resource.");
                recordErrors.Add($"{resourceType}/{record.SourceResourceId ?? id ?? "unknown"}: {message}");
            }
        }
    }

    /// <summary>
    /// Reads a batch-response Bundle's <c>entry[].response.status</c> ("200 OK", "422 Unprocessable Entity", ...)
    /// and, for a failed entry, its <c>response.outcome</c> (an <c>OperationOutcome</c>) if the server populated one
    /// — not every FHIR server does, so this falls back to the raw status string rather than failing to parse.
    /// </summary>
    private static List<(bool IsSuccess, string? Diagnostics)> ParseBatchResponseEntries(string responseBody)
    {
        var results = new List<(bool, string?)>();

        if (string.IsNullOrWhiteSpace(responseBody)
            || JsonNode.Parse(responseBody) is not JsonObject responseBundle
            || responseBundle["entry"] is not JsonArray responseEntries)
        {
            return results;
        }

        foreach (var entryNode in responseEntries)
        {
            var status = entryNode?["response"]?["status"]?.GetValue<string>();
            var isSuccess = status is not null && status.TrimStart().StartsWith("2", StringComparison.Ordinal);

            string? diagnostics = null;
            if (!isSuccess)
            {
                var outcome = entryNode?["response"]?["outcome"] as JsonObject;
                diagnostics = (outcome?["issue"] as JsonArray)?
                    .Select(issue => issue?["diagnostics"]?.GetValue<string>())
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
                diagnostics ??= status;
            }

            results.Add((isSuccess, diagnostics));
        }

        return results;
    }

    /// <summary>
    /// Produces a (resourceType, id, body) triple to PUT. Prefers the normalized FHIR resource; reconciles its
    /// <c>id</c> to a stable value so URL and body agree. Falls back to the flattened payload for non-FHIR flows.
    /// </summary>
    private static (string ResourceType, string Id, string Body) BuildFhirResource(MappedDestinationRecord record)
    {
        if (TryParseFhirResource(record, out var resourceType, out var resource))
        {
            var id = resource["id"]!.GetValue<string>();
            return (resourceType, Uri.EscapeDataString(id), resource.ToJsonString(JsonOptions));
        }

        // Non-FHIR fallback: post the flattened mapped payload to a permissive ingestion endpoint.
        var fallbackId = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? Guid.NewGuid().ToString("N")
            : Uri.EscapeDataString(record.SourceResourceId);
        return (record.ResourceType, fallbackId, MappedDestinationSerialization.ToJson(record));
    }

    /// <summary>
    /// Shared by <see cref="BuildFhirResource"/> (individual mode) and the bundle-mode path: parses
    /// <see cref="MappedDestinationRecord.SourceJson"/> as a FHIR resource and reconciles its <c>id</c> to a stable
    /// value (the resource's own <c>id</c> if present, else <see cref="MappedDestinationRecord.SourceResourceId"/>,
    /// else a new GUID) so every caller sees the exact same id this record will be written under. Returns false for
    /// the non-FHIR fallback flow (no <c>SourceJson</c>, or it doesn't parse as an object with a resourceType).
    /// </summary>
    private static bool TryParseFhirResource(MappedDestinationRecord record, out string resourceType, out JsonObject resource)
    {
        if (!string.IsNullOrWhiteSpace(record.SourceJson)
            && JsonNode.Parse(record.SourceJson) is JsonObject parsed
            && parsed["resourceType"]?.GetValue<string>() is { Length: > 0 } type)
        {
            var id = parsed["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                id = string.IsNullOrWhiteSpace(record.SourceResourceId)
                    ? Guid.NewGuid().ToString("N")
                    : record.SourceResourceId;
                parsed["id"] = id;
            }

            resourceType = type;
            resource = parsed;
            return true;
        }

        resourceType = string.Empty;
        resource = null!;
        return false;
    }
}
