using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Audit;

/// <summary>
/// Records application-level user activity (who did what, when, from where) into the append-only,
/// tamper-evident <c>UserActivityAuditLogs</c> trail. Distinct from <see cref="IOperationalAuditService"/>,
/// which records pipeline/system events.
/// </summary>
public interface IUserActivityAuditService
{
    Task RecordAsync(RecordUserActivityRequest request, CancellationToken cancellationToken);

    Task<PagedResult<UserActivityAuditLogDto>> GetPagedAsync(
        UserActivityLogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed record UserActivityLogFilter(
    string? Category,
    string? Status,
    Guid? UserId,
    string? Search);
