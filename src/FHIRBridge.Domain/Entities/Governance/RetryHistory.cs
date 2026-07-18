using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of one retry attempt against a transient failure.</summary>
public sealed class RetryHistory : Entity<Guid>, IAppendOnlyEntity
{
    private RetryHistory()
    {
    }

    public RetryHistory(
        Guid id,
        DateTime occurredOnUtc,
        string context,
        int retryNumber,
        int delayMilliseconds,
        string reason,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Context = context;
        RetryNumber = retryNumber;
        DelayMilliseconds = delayMilliseconds;
        Reason = reason;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string Context { get; private set; } = default!;
    public int RetryNumber { get; private set; }
    public int DelayMilliseconds { get; private set; }
    public string Reason { get; private set; } = default!;
    public string? CorrelationId { get; private set; }
}
