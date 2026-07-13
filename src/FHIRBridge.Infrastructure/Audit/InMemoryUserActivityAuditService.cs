using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Audit;

public sealed class InMemoryUserActivityAuditService : IUserActivityAuditService
{
    private readonly List<UserActivityAuditLog> _entries = [];
    private readonly object _gate = new();

    public Task RecordAsync(RecordUserActivityRequest request, CancellationToken cancellationToken)
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

        lock (_gate)
        {
            entity.SealChain(_entries.Count > 0 ? _entries[^1].EntryHash : null);
            _entries.Add(entity);
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<UserActivityAuditLogDto>> GetPagedAsync(
        UserActivityLogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var query = _entries.AsEnumerable();

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
                    x.UserEmail.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    x.Activity.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = query.OrderByDescending(x => x.OccurredOnUtc).ToList();
            var take = Math.Clamp(pageSize, 1, 200);
            var skip = Math.Max(0, (page - 1) * take);

            return Task.FromResult(new PagedResult<UserActivityAuditLogDto>(
                ordered.Skip(skip).Take(take).Select(ToDto).ToList(),
                ordered.Count,
                page,
                take));
        }
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
