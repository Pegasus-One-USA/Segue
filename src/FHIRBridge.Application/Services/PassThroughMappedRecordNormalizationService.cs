using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public sealed class PassThroughMappedRecordNormalizationService : IMappedRecordNormalizationService
{
    public Task<MappedDestinationRecord> NormalizeAsync(
        MappedRecordNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(request.Record);
    }
}
