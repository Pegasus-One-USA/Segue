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
        string? correlationId,
        string? body = null,
        string? attachmentNames = null)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        NotificationType = notificationType;
        Recipient = recipient;
        Subject = subject;
        Status = status;
        Error = error;
        CorrelationId = correlationId;
        Body = body;
        AttachmentNames = attachmentNames;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string NotificationType { get; private set; } = default!;
    public string Recipient { get; private set; } = default!;
    public string? Subject { get; private set; }
    public string Status { get; private set; } = default!;
    public string? Error { get; private set; }
    public string? CorrelationId { get; private set; }

    /// <summary>The rendered email body (or other notification content), for diagnosing what was actually sent —
    /// e.g. an export-delivery email whose recipient reports never receiving it.</summary>
    public string? Body { get; private set; }

    /// <summary>Comma-separated attachment file names, if any.</summary>
    public string? AttachmentNames { get; private set; }
}
