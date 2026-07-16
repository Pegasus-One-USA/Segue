using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Audit;

public sealed class InMemoryOperationalAuditService : IOperationalAuditService
{
    private readonly List<OperationalAuditLogDto> _logs = [];
    private readonly object _gate = new();

    public Task RecordAsync(
        RecordOperationalAuditLogRequest request,
        CancellationToken cancellationToken)
    {
        var log = new OperationalAuditLogDto(
            Guid.NewGuid(),
            request.PipelineRunId,
            request.ResourcePipelineRouteId,
            request.SourceConnectionId,
            request.DestinationId,
            request.MappingProfileId,
            request.ResourceType,
            request.Action,
            request.Status,
            request.Message,
            request.ResourceCount,
            request.TriggeredBy,
            request.CorrelationId,
            DateTime.UtcNow,
            request.Severity);

        lock (_gate)
        {
            _logs.Add(log);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OperationalAuditLogDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count, 1, 500);

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OperationalAuditLogDto>>(
                _logs
                    .OrderByDescending(x => x.OccurredOnUtc)
                    .Take(take)
                    .ToList());
        }
    }

    public Task<PagedResult<OperationalAuditLogDto>> GetPagedAsync(
        OperationalAuditLogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var query = _logs.AsEnumerable();

            if (filter.PipelineRunId.HasValue)
            {
                query = query.Where(x => x.PipelineRunId == filter.PipelineRunId);
            }

            if (!string.IsNullOrWhiteSpace(filter.ResourceType))
            {
                query = query.Where(x => x.ResourceType == filter.ResourceType);
            }

            if (!string.IsNullOrWhiteSpace(filter.Action))
            {
                query = query.Where(x => x.Action == filter.Action);
            }

            if (!string.IsNullOrWhiteSpace(filter.Status))
            {
                query = query.Where(x => x.Status == filter.Status);
            }

            if (!string.IsNullOrWhiteSpace(filter.Severity))
            {
                query = query.Where(x => x.Severity == filter.Severity);
            }

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var search = filter.Search;
                query = query.Where(x =>
                    x.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    x.Action.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = query.OrderByDescending(x => x.OccurredOnUtc).ToList();
            var take = Math.Clamp(pageSize, 1, 200);
            var skip = Math.Max(0, (page - 1) * take);

            return Task.FromResult(new PagedResult<OperationalAuditLogDto>(
                ordered.Skip(skip).Take(take).ToList(),
                ordered.Count,
                page,
                take));
        }
    }
}
