namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// A security-relevant event outside normal auth/config flows (lockouts, anomalies, denials).
/// Deliberately NOT append-only: <see cref="Resolved"/> is expected to change as a security
/// officer triages it, unlike the strictly immutable audit/data-access/authentication logs.
/// </summary>
public sealed class SecurityEvent : SharedKernel.Abstractions.Entity<Guid>
{
    private SecurityEvent()
    {
    }

    public SecurityEvent(
        Guid id,
        DateTime occurredOnUtc,
        string severity,
        string eventType,
        string? userEmail,
        string? ipAddress,
        string? details,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Severity = severity;
        EventType = eventType;
        UserEmail = userEmail;
        IpAddress = ipAddress;
        Details = details;
        CorrelationId = correlationId;
        Resolved = false;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string Severity { get; private set; } = default!;
    public string EventType { get; private set; } = default!;
    public string? UserEmail { get; private set; }
    public string? IpAddress { get; private set; }
    public string? Details { get; private set; }
    public string? CorrelationId { get; private set; }
    public bool Resolved { get; private set; }

    public void MarkResolved() => Resolved = true;
}
