namespace FHIRBridge.Application.Messaging;

/// <summary>
/// A webhook payload accepted by the API and published to the messaging transport for asynchronous processing.
/// <see cref="MessageId"/> is the idempotency key (derived from webhook and payload hash).
/// </summary>
public sealed record WebhookIngestionCommand(
    Guid WebhookConfigurationId,
    string ResourceJson,
    string PayloadHash,
    string? TriggeredBy,
    string? CorrelationId,
    string MessageId);
