using FHIRBridge.Application.Abstractions.Audit;
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
            DateTime.UtcNow);

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
}
