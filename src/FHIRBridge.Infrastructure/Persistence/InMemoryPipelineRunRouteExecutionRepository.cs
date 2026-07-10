using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryPipelineRunRouteExecutionRepository : IPipelineRunRouteExecutionRepository
{
    private readonly List<PipelineRunRouteExecutionDto> _executions = [];
    private readonly object _gate = new();

    public Task<Guid> CreateRunningAsync(
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
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        lock (_gate)
        {
            _executions.Add(new PipelineRunRouteExecutionDto(
                id,
                pipelineRunId,
                pipelineName,
                sourceName,
                sourceSystemType,
                Domain.Entities.PipelineRunRouteExecutionStatus.Running,
                startedOnUtc,
                null,
                triggeredBy,
                triggerType,
                0,
                0,
                0,
                null));
        }

        return Task.FromResult(id);
    }

    public Task CompleteAsync(
        Guid routeExecutionId,
        string status,
        int extractedCount,
        int mappedCount,
        int writtenCount,
        string? errorMessage,
        DateTime completedOnUtc,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _executions.FindIndex(x => x.Id == routeExecutionId);
            if (index >= 0)
            {
                _executions[index] = _executions[index] with
                {
                    Status = status,
                    ExtractedCount = extractedCount,
                    MappedCount = mappedCount,
                    WrittenCount = writtenCount,
                    ErrorMessage = errorMessage,
                    CompletedOnUtc = completedOnUtc,
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<PipelineRunRouteExecutionDto>> GetPagedAsync(
        PipelineRunRouteExecutionFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var query = _executions.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(filter.Status))
            {
                query = query.Where(x => x.Status == filter.Status);
            }

            if (!string.IsNullOrWhiteSpace(filter.Source))
            {
                query = query.Where(x => x.SourceName == filter.Source || x.SourceSystemType == filter.Source);
            }

            if (!string.IsNullOrWhiteSpace(filter.TriggeredBy))
            {
                query = query.Where(x => x.TriggeredBy == filter.TriggeredBy || x.TriggerType == filter.TriggeredBy);
            }

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var search = filter.Search;
                query = query.Where(x =>
                    x.PipelineName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    x.SourceName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = query.OrderByDescending(x => x.StartedOnUtc).ToList();
            var take = Math.Clamp(pageSize, 1, 200);
            var skip = Math.Max(0, (page - 1) * take);

            return Task.FromResult(new PagedResult<PipelineRunRouteExecutionDto>(
                ordered.Skip(skip).Take(take).ToList(),
                ordered.Count,
                page,
                take));
        }
    }

    public Task<PipelineRunRouteExecutionDto?> GetByIdAsync(
        Guid routeExecutionId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_executions.FirstOrDefault(x => x.Id == routeExecutionId));
        }
    }
}
