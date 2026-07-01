namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>Durable idempotency record — one row per processed message id. Shared across all worker instances.</summary>
public sealed class ProcessedMessage
{
    public string MessageId { get; set; } = default!;
    public DateTime ProcessedOnUtc { get; set; }
}
