using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
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

    public async Task<PagedResult<UserActivityAuditLogDto>> GetPagedAsync(
        UserActivityLogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.UserActivityAuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            query = query.Where(x => x.Category == filter.Category);
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(x => x.Status == filter.Status);
        }

        if (filter.UserId.HasValue)
        {
            query = query.Where(x => x.UserId == filter.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x =>
                EF.Functions.Like(x.UserEmail, $"%{search}%") ||
                EF.Functions.Like(x.Activity, $"%{search}%"));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var records = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(skip)
            .Take(take)
            .Select(x => ToDto(x))
            .ToListAsync(cancellationToken);

        return new PagedResult<UserActivityAuditLogDto>(records, totalCount, page, take);
    }

    private static UserActivityAuditLogDto ToDto(UserActivityAuditLog entity) => new(
        entity.Id,
        entity.UserId,
        entity.UserEmail,
        entity.Category,
        entity.Activity,
        entity.Status,
        entity.EntityName,
        entity.EntityId,
        entity.IpAddress,
        entity.UserAgent,
        entity.HttpMethod,
        entity.RequestPath,
        entity.Details,
        entity.CorrelationId,
        entity.SessionId,
        entity.FailureReason,
        entity.Severity,
        entity.OccurredOnUtc);
}
