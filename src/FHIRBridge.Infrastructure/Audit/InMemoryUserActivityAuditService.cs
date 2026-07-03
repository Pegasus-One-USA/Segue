using FHIRBridge.Application.Abstractions.Audit;
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
}
