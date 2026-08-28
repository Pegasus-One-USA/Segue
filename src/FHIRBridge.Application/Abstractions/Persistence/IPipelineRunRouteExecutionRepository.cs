using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Persists one row per route processed within a configured pipeline run — the grain the Execution History
/// screen renders (pipeline name, source, status, duration, triggered-by). See
/// <see cref="FHIRBridge.Domain.Entities.PipelineRunRouteExecution"/> for the durable shape.
/// </summary>
public interface IPipelineRunRouteExecutionRepository
{
    Task<Guid> CreateRunningAsync(
        Guid pipelineRunId,
        Guid routeId,
        Guid mappingProfileId,
        string pipelineName,
        Guid sourceConnectionId,
        string sourceName,
        string sourceSystemType,
        string? triggeredBy,
        string? triggerType,
        DateTime startedOnUtc,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        Guid routeExecutionId,
        string status,
        int extractedCount,
        int mappedCount,
        int writtenCount,
        string? errorMessage,
        DateTime completedOnUtc,
        CancellationToken cancellationToken,
        string? errorReferenceId = null);

    Task<PagedResult<PipelineRunRouteExecutionDto>> GetPagedAsync(
        PipelineRunRouteExecutionFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PipelineRunRouteExecutionDto?> GetByIdAsync(
        Guid routeExecutionId,
        CancellationToken cancellationToken);
}

public sealed record PipelineRunRouteExecutionFilter(
    string? Status,
    string? Source,
    string? TriggeredBy,
    string? Search);
