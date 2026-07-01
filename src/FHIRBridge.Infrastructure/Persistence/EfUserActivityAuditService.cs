using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfUserActivityAuditService : IUserActivityAuditService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfUserActivityAuditService(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(RecordUserActivityRequest request, CancellationToken cancellationToken)
    {
        var previousHash = await GetLastHashAsync(request.TenantId, cancellationToken);

        var entry = new UserActivityAuditLog(
            request.TenantId,
            request.UserId,
            request.UserEmail ?? "unknown",
            request.Category,
            request.Activity,
            request.Status,
            request.EntityName,
            request.EntityId,
            request.IpAddress,
            request.UserAgent,
            request.HttpMethod,
            request.RequestPath,
            request.Details,
            request.CorrelationId,
            request.SessionId,
            request.FailureReason,
            request.Severity,
            DateTime.UtcNow);

        entry.SealChain(previousHash);

        await _dbContext.UserActivityAuditLogs.AddAsync(entry, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> GetLastHashAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        return await _dbContext.UserActivityAuditLogs
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.OccurredOnUtc)
            .Select(x => x.EntryHash)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
