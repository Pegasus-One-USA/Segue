namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// Retry policy for command handlers (bound from <c>Messaging:Processing</c>). Transient failures are retried with
/// exponential backoff; once attempts are exhausted the handler throws so the transport dead-letters the message.
/// </summary>
public sealed class MessageProcessingOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int BaseDelayMilliseconds { get; set; } = 500;
}
