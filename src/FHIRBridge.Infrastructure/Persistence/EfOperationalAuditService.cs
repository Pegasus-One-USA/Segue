using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfOperationalAuditService : IOperationalAuditService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfOperationalAuditService(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(RecordOperationalAuditLogRequest request, CancellationToken cancellationToken)
    {
        var entry = new OperationalAuditLog(
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

        await _dbContext.OperationalAuditLogs.AddAsync(entry, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationalAuditLogDto>> GetRecentAsync(
        Guid tenantId,
        int count,
        CancellationToken cancellationToken)
    {
        return await _dbContext.OperationalAuditLogs
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(count)
            .Select(x => new OperationalAuditLogDto(
                x.Id,
                x.TenantId,
                x.PipelineRunId,
                x.ResourcePipelineRouteId,
                x.SourceConnectionId,
                x.DestinationId,
                x.MappingProfileId,
                x.ResourceType,
                x.Action,
                x.Status,
                x.Message,
                x.ResourceCount,
                x.TriggeredBy,
                x.CorrelationId,
                x.OccurredOnUtc))
            .ToListAsync(cancellationToken);
    }
}
