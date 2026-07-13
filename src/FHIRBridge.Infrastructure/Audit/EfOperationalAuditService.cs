using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
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

    public async Task<PagedResult<OperationalAuditLogDto>> GetPagedAsync(
        OperationalAuditLogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.OperationalAuditLogs.AsNoTracking().AsQueryable();

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

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x =>
                EF.Functions.Like(x.Message, $"%{search}%") ||
                EF.Functions.Like(x.Action, $"%{search}%"));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var records = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(skip)
            .Take(take)
            .Select(x => OperationalAuditMapper.ToDto(x))
            .ToListAsync(cancellationToken);

        return new PagedResult<OperationalAuditLogDto>(records, totalCount, page, take);
    }
}
