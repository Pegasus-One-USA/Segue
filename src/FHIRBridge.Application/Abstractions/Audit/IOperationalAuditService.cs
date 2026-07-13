using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Audit;

public interface IOperationalAuditService
{
    Task RecordAsync(
        RecordOperationalAuditLogRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationalAuditLogDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken);

    Task<PagedResult<OperationalAuditLogDto>> GetPagedAsync(
        OperationalAuditLogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed record OperationalAuditLogFilter(
    Guid? PipelineRunId,
    string? ResourceType,
    string? Action,
    string? Status,
    string? Search);
