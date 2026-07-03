using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Audit;

public sealed class EfUserActivityAuditService : IUserActivityAuditService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfUserActivityAuditService(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(RecordUserActivityRequest request, CancellationToken cancellationToken)
    {
        var entity = new UserActivityAuditLog(
            request.UserId,
            request.UserEmail,
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

        // Single tamper-evident hash chain across all activity entries.
        var previousHash = await _dbContext.UserActivityAuditLogs
            .AsNoTracking()
            .OrderByDescending(x => x.OccurredOnUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => x.EntryHash)
            .FirstOrDefaultAsync(cancellationToken);

        entity.SealChain(previousHash);

        await _dbContext.UserActivityAuditLogs.AddAsync(entity, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
