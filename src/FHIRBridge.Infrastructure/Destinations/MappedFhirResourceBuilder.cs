using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Shared by every FHIR-native destination writer (FHIR repository, Azure Health Data Services): produces a
/// (resourceType, id, body) triple from a <see cref="MappedDestinationRecord"/>. Prefers the normalized source FHIR
/// resource (<see cref="MappedDestinationRecord.SourceJson"/>), reconciling its <c>id</c> to a stable value so the
/// resource body and the write URL agree. Falls back to the flattened mapped payload for non-FHIR flows.
/// </summary>
internal static class MappedFhirResourceBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static (string ResourceType, string Id, string Body) Build(MappedDestinationRecord record)
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

        var fallbackId = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? Guid.NewGuid().ToString("N")
            : Uri.EscapeDataString(record.SourceResourceId);
        return (record.ResourceType, fallbackId, MappedDestinationSerialization.ToJson(record));
    }
}
