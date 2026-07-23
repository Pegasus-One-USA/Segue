using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Pipeline;

/// <summary>
/// Records the full fetch/normalize/map/store history for resources processed within a route execution, so
/// the Execution History screen can answer "what was fetched, what was mapped, what was stored" for a run.
/// Holds PHI-bearing payloads — implementations are expected to encrypt the payload fields at rest and implement
/// <c>IPurgeableStore</c> so they're swept by the retention policy.
/// </summary>
public interface IExecutionResourceHistoryRecorder
{
    Task RecordFetchedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string fetchedJson,
        CancellationToken cancellationToken);

    Task RecordNormalizedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string normalizedJson,
        IReadOnlyCollection<string> appliedProfiles,
        IReadOnlyCollection<string> warnings,
        double? dataQualityScore,
        string? masterPatientId,
        CancellationToken cancellationToken);

    Task RecordMappedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken);

    Task RecordStoredAsync(
        Guid routeExecutionId,
        IReadOnlyCollection<string?> sourceResourceIds,
        CancellationToken cancellationToken);

    Task RecordFailedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string errorMessage,
        CancellationToken cancellationToken);

    Task<PagedResult<PipelineRunResourceHistoryDto>> GetPagedAsync(
        Guid routeExecutionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
