using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Terminology;

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
        var baseUrl = (destination.Target ?? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)).TrimEnd('/');
        var httpClient = _httpClientFactory.CreateClient(nameof(MappedFhirRepositoryDestinationWriter));

        foreach (var record in records)
        {
            var (resourceType, resourceId, body) = BuildFhirResource(record);
            var endpoint = $"{baseUrl}/{resourceType}/{resourceId}";
            using var content = new StringContent(body, Encoding.UTF8, "application/fhir+json");
            using var response = await httpClient.PutAsync(endpoint, content, cancellationToken);
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

            StripVersionFromStableCodings(resource);

            return (resourceType, Uri.EscapeDataString(id!), resource.ToJsonString(JsonOptions));
        }

        // Non-FHIR fallback: post the flattened mapped payload to a permissive ingestion endpoint.
        var fallbackId = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? Guid.NewGuid().ToString("N")
            : Uri.EscapeDataString(record.SourceResourceId);
        return (record.ResourceType, fallbackId, MappedDestinationSerialization.ToJson(record));
    }

    /// <summary>
    /// Recursively strips <c>version</c> from any <c>Coding</c> in a <c>coding</c> array whose <c>system</c> is one of
    /// <see cref="StableCodeSystemVersions.StableCodeSystemUrls"/>, so a destination FHIR server (e.g. Aidbox) matches
    /// on <c>system</c> alone instead of rejecting a code purely because its version label doesn't match whatever
    /// CodeSystem version the destination has loaded. Leaves codings on any other system — including CodeSystems
    /// known to have real cross-version code drift — untouched.
    /// </summary>
    private static void StripVersionFromStableCodings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["coding"] is JsonArray codings)
                {
                    foreach (var coding in codings.OfType<JsonObject>())
                    {
                        var system = coding["system"] is JsonValue systemValue && systemValue.TryGetValue<string>(out var systemText)
                            ? systemText
                            : null;
                        if (system is not null && StableCodeSystemVersions.StableCodeSystemUrls.Contains(system))
                        {
                            coding.Remove("version");
                        }
                    }
                }

                foreach (var child in obj)
                {
                    StripVersionFromStableCodings(child.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    StripVersionFromStableCodings(item);
                }

                break;
        }
    }
}
