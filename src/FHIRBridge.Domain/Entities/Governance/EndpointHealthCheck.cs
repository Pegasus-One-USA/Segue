using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of one periodic connectivity check against a configured endpoint.</summary>
public sealed class EndpointHealthCheck : Entity<Guid>, IAppendOnlyEntity
{
    private EndpointHealthCheck()
    {
    }

    public EndpointHealthCheck(
        Guid id,
        DateTime occurredOnUtc,
        string endpointName,
        string endpointType,
        string status,
        long latencyMs,
        string? message)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        EndpointName = endpointName;
        EndpointType = endpointType;
        Status = status;
        LatencyMs = latencyMs;
        Message = message;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string EndpointName { get; private set; } = default!;
    public string EndpointType { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public long LatencyMs { get; private set; }
    public string? Message { get; private set; }
}
