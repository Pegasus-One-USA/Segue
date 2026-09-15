using System.Text.Json;
using System.Text.Json.Serialization;

namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <summary>
/// One participating resource type in a multi-resource ApiEndpoint destination (see
/// <see cref="ApiEndpointMultiResourceMode"/>) — every resource type combined into the destination's outgoing
/// document needs exactly one of these, including the root/parent resource itself. Parsed from the destination's
/// <c>dest_apiResourceRelationsJson</c> connection-metadata field as a JSON array; never touches
/// <c>MappingProfile</c> or any other destination type's settings.
/// </summary>
/// <param name="ResourceType">The FHIR resource type this entry describes — must match a MappingProfile's own
/// ResourceType for one of the mapping profiles routed at this destination.</param>
/// <param name="ParentResourceType">Null for a flat/root resource (Nested mode's actual root, or any resource in
/// Flat mode). Set to another entry's ResourceType to nest this resource's records under that parent instead.</param>
/// <param name="CorrelationColumn">Nested only: the mapped column ON THIS resource holding the value that links
/// each of its records to one parent record — typically a flattened reference field (e.g. Encounter's mapped
/// "PatientId" column, holding the value from Encounter.subject.reference).</param>
/// <param name="ParentKeyColumn">Nested only: the mapped column on the PARENT resource that
/// <see cref="CorrelationColumn"/>'s value is matched against for each parent record (typically the parent's own
/// mapped id column).</param>
/// <param name="NestKey">The JSON key this resource's records are written under — an array of this resource's
/// per-record objects nested inside the parent object (Nested), or a top-level sibling array (Flat/root).</param>
public sealed record ApiEndpointResourceRelation(
    string ResourceType,
    string? ParentResourceType,
    string? CorrelationColumn,
    string? ParentKeyColumn,
    string NestKey)
{
    [JsonIgnore]
    public bool IsRoot => string.IsNullOrWhiteSpace(ParentResourceType);

    public static IReadOnlyList<ApiEndpointResourceRelation> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<ApiEndpointResourceRelation>>(
                json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return parsed ?? [];
        }
        catch (JsonException)
        {
            // Malformed config is treated as "not configured" rather than failing the whole destination parse —
            // ApiEndpointSettings.Parse already tolerates a malformed dictionary field the same way (see
            // ParseDictionary) — the destination simply falls back to single-resource behavior until the JSON
            // is fixed, instead of every route through it failing outright.
            return [];
        }
    }
}
