using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of a scheduler dispatch decision — "the scheduler recognized N due route(s) and dispatched them".</summary>
public sealed class SchedulerHistory : Entity<Guid>, IAppendOnlyEntity
{
    private SchedulerHistory()
    {
    }

    public SchedulerHistory(
        Guid id,
        string schedulerId,
        DateTime runTimeUtc,
        string status,
        int routeCount,
        string? correlationId)
    {
        Id = id;
        SchedulerId = schedulerId;
        RunTimeUtc = runTimeUtc;
        Status = status;
        RouteCount = routeCount;
        CorrelationId = correlationId;
    }

    public string SchedulerId { get; private set; } = default!;
    public DateTime RunTimeUtc { get; private set; }
    public string Status { get; private set; } = default!;
    public int RouteCount { get; private set; }
    public string? CorrelationId { get; private set; }
}
