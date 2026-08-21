using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Mapping;
using FHIRBridge.Application.Services.Mapping.Internal;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Mapping;

public sealed class SchemaMatchingService : ISchemaMatchingService
{
    private readonly ISchemaMappingRepository _repository;

    public SchemaMatchingService(ISchemaMappingRepository repository)
    {
        _repository = repository;
    }

    public async Task<List<FieldMappingSuggestion>> SuggestMappingsAsync(
        string sourceSystem,
        string resourceType,
        string destinationTableName,
        JsonDocument sourceJson,
        List<FhirFieldMetadata> destinationFields,
        CancellationToken cancellationToken = default)
    {
        var approved = await _repository.GetApprovedAsync(sourceSystem, resourceType, destinationTableName, cancellationToken);
        var approvedByField = approved.ToDictionary(m => m.DestinationField, StringComparer.OrdinalIgnoreCase);

        var sourceFields = JsonSchemaExtractor.Extract(sourceJson);
        var suggestions = new FieldMappingSuggestion[destinationFields.Count];

        // Destination fields are independent of one another — score them in parallel. Approved overrides are
        // resolved first per-field (no scoring needed) so only genuinely unmapped fields pay the full comparison cost.
        await Task.Run(
            () => Parallel.For(
                0,
                destinationFields.Count,
                new ParallelOptions { CancellationToken = cancellationToken },
                i =>
                {
                    var destination = destinationFields[i];
                    suggestions[i] = approvedByField.TryGetValue(destination.Name, out var approvedMapping)
                        ? ApprovedSuggestion(destination.Name, approvedMapping)
                        : BestMatch(destination, sourceFields);
                }),
            cancellationToken);

        return suggestions.ToList();
    }

    public async Task SaveApprovedMappingAsync(
        string sourceSystem,
        string resourceType,
        string destinationTableName,
        IReadOnlyList<ApprovedMappingRequest> approvedMappings,
        CancellationToken cancellationToken = default)
    {
        foreach (var request in approvedMappings)
        {
            var status = request.Approved ? SchemaMappingStatus.Approved : SchemaMappingStatus.Rejected;
            var existing = await _repository.FindAsync(
                sourceSystem, resourceType, destinationTableName, request.DestinationField, cancellationToken);

            if (existing is null)
            {
                var mapping = new SchemaMapping(
                    sourceSystem, resourceType, destinationTableName,
                    request.SourceField, request.DestinationField, request.Confidence, status);
                await _repository.AddAsync(mapping, cancellationToken);
            }
            else
            {
                existing.UpdateMatch(request.SourceField, request.Confidence, status);
                await _repository.UpdateAsync(existing, cancellationToken);
            }
        }
    }

    public async Task<List<SchemaMapping>> GetApprovedMappingsAsync(
        string sourceSystem,
        string resourceType,
        string destinationTableName,
        CancellationToken cancellationToken = default)
    {
        var approved = await _repository.GetApprovedAsync(sourceSystem, resourceType, destinationTableName, cancellationToken);
        return approved.ToList();
    }

    private static FieldMappingSuggestion ApprovedSuggestion(string destinationField, SchemaMapping approvedMapping) =>
        new(destinationField, approvedMapping.SourceField, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, "Approved mapping override");

    private static FieldMappingSuggestion BestMatch(
        FhirFieldMetadata destination, IReadOnlyList<ExtractedSourceField> sourceFields)
    {
        if (sourceFields.Count == 0)
        {
            return new FieldMappingSuggestion(destination.Name, null, 0, 0, 0, 0, 0, 0, "No source fields available");
        }

        var normalizedDestination = FieldNormalizer.Normalize(destination.Name);

        FieldMappingSuggestion? best = null;
        var bestConfidence = -1.0;

        foreach (var source in sourceFields)
        {
            var score = MappingScorer.Score(source, normalizedDestination, destination.DataType);
            if (score.Confidence <= bestConfidence)
            {
                continue;
            }

            bestConfidence = score.Confidence;
            best = new FieldMappingSuggestion(
                destination.Name,
                source.Path,
                score.Confidence,
                score.NameScore,
                score.SynonymScore,
                score.TypeScore,
                score.ValueScore,
                score.StructureScore,
                score.Reason);
        }

        return best!;
    }
}
