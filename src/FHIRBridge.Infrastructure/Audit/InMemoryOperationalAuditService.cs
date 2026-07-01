using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Audit;

public sealed class InMemoryOperationalAuditService : IOperationalAuditService
{
    private readonly ConcurrentDictionary<Guid, List<OperationalAuditLogDto>> _logs = new();

    public Task RecordAsync(
        RecordOperationalAuditLogRequest request,
        CancellationToken cancellationToken)
    {
        var log = new OperationalAuditLogDto(
            Guid.NewGuid(),
            request.TenantId,
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

        var tenantLogs = _logs.GetOrAdd(request.TenantId, _ => []);

        lock (tenantLogs)
        {
            tenantLogs.Add(log);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OperationalAuditLogDto>> GetRecentAsync(
        Guid tenantId,
        int count,
        CancellationToken cancellationToken)
    {
        if (!_logs.TryGetValue(tenantId, out var tenantLogs))
        {
            return Task.FromResult<IReadOnlyList<OperationalAuditLogDto>>([]);
        }

        var take = Math.Clamp(count, 1, 500);

        lock (tenantLogs)
        {
            return Task.FromResult<IReadOnlyList<OperationalAuditLogDto>>(
                tenantLogs
                    .OrderByDescending(x => x.OccurredOnUtc)
                    .Take(take)
                    .ToList());
        }
    }
}
