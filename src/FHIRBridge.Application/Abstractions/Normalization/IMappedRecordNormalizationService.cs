using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Normalization;

public interface IMappedRecordNormalizationService
{
    Task<MappedDestinationRecord> NormalizeAsync(
        MappedRecordNormalizationRequest request,
        CancellationToken cancellationToken);
}

public sealed record MappedRecordNormalizationRequest(
    string SourceJson,
    MappedDestinationRecord Record,
    IReadOnlyCollection<MappingFieldDto> MappingFields);
