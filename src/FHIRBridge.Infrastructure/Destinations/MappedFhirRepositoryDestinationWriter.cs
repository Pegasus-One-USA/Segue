using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
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
                resource["id"] = id;
            }

            return (resourceType, Uri.EscapeDataString(id!), resource.ToJsonString(JsonOptions));
        }

        // Non-FHIR fallback: post the flattened mapped payload to a permissive ingestion endpoint.
        var fallbackId = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? Guid.NewGuid().ToString("N")
            : Uri.EscapeDataString(record.SourceResourceId);
        return (record.ResourceType, fallbackId, MappedDestinationSerialization.ToJson(record));
    }
}
