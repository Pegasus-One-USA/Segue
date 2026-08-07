using System.Text.Json;
using FHIRBridge.Application.DTOs.Mapping;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services.Mapping;

/// <summary>
/// Destination-driven schema matching: given a destination table/resource's columns and a sample of source
/// EHR JSON, suggests the best-matching source field for every destination column with an explainable,
/// weighted confidence score. Previously approved matches (see <see cref="SaveApprovedMappingAsync"/>) are
/// reused automatically and override the scoring algorithm.
/// </summary>
public interface ISchemaMatchingService
{
    Task<List<FieldMappingSuggestion>> SuggestMappingsAsync(
        string sourceSystem,
        string resourceType,
        string destinationTableName,
        JsonDocument sourceJson,
        List<FhirFieldMetadata> destinationFields,
        CancellationToken cancellationToken = default);

    Task SaveApprovedMappingAsync(
        string sourceSystem,
        string resourceType,
        string destinationTableName,
        IReadOnlyList<ApprovedMappingRequest> approvedMappings,
        CancellationToken cancellationToken = default);

    Task<List<SchemaMapping>> GetApprovedMappingsAsync(
        string sourceSystem,
        string resourceType,
        string destinationTableName,
        CancellationToken cancellationToken = default);
}
