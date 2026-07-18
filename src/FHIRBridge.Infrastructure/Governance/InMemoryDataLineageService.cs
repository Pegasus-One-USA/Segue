using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No database configured — nothing to trace lineage for.</summary>
public sealed class InMemoryDataLineageService : IDataLineageService
{
    public Task<DataLineageDto?> GetLineageAsync(Guid resourceRecordId, CancellationToken cancellationToken)
        => Task.FromResult<DataLineageDto?>(null);

    public Task<LineageFieldValueDto> RevealFieldValueAsync(
        Guid resourceRecordId, string targetField, CancellationToken cancellationToken)
        => Task.FromResult(new LineageFieldValueDto(targetField, null));
}
