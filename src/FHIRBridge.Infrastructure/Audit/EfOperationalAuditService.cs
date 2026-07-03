using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Audit;

public sealed class EfOperationalAuditService : IOperationalAuditService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfOperationalAuditService(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(
        RecordOperationalAuditLogRequest request,
        CancellationToken cancellationToken)
    {
        await _dbContext.OperationalAuditLogs.AddAsync(
            OperationalAuditMapper.ToEntity(request),
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationalAuditLogDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count, 1, 500);

        return await _dbContext.OperationalAuditLogs
            .AsNoTracking()
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(take)
            .Select(x => OperationalAuditMapper.ToDto(x))
            .ToListAsync(cancellationToken);
    }
}
