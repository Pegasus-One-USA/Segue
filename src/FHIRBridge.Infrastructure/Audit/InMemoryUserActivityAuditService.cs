using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Audit;

public sealed class InMemoryUserActivityAuditService : IUserActivityAuditService
{
    private readonly ConcurrentDictionary<Guid, List<UserActivityAuditLog>> _entries = new();

    public Task RecordAsync(RecordUserActivityRequest request, CancellationToken cancellationToken)
    {
        var entity = new UserActivityAuditLog(
            request.TenantId,
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

        // Null tenant shares one chain bucket (Guid.Empty).
        var chain = _entries.GetOrAdd(request.TenantId ?? Guid.Empty, _ => []);

        lock (chain)
        {
            entity.SealChain(chain.Count > 0 ? chain[^1].EntryHash : null);
            chain.Add(entity);
        }

        return Task.CompletedTask;
    }
}
