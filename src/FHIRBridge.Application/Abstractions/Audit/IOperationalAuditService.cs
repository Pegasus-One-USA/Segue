using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Audit;

public interface IOperationalAuditService
{
    Task RecordAsync(
        RecordOperationalAuditLogRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationalAuditLogDto>> GetRecentAsync(
        Guid tenantId,
        int count,
        CancellationToken cancellationToken);
}
