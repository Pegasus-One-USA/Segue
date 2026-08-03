using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of one outbound notification (currently: export-delivery email).</summary>
public sealed class NotificationHistory : Entity<Guid>, IAppendOnlyEntity
{
    private NotificationHistory()
    {
    }

    public NotificationHistory(
        Guid id,
        DateTime occurredOnUtc,
        string notificationType,
        string recipient,
        string? subject,
        string status,
        string? error,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        NotificationType = notificationType;
        Recipient = recipient;
        Subject = subject;
        Status = status;
        Error = error;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string NotificationType { get; private set; } = default!;
    public string Recipient { get; private set; } = default!;
    public string? Subject { get; private set; }
    public string Status { get; private set; } = default!;
    public string? Error { get; private set; }
    public string? CorrelationId { get; private set; }
}
