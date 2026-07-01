namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>
/// Tracks processed message ids so redelivered or duplicate commands are handled exactly once.
/// </summary>
public interface IProcessedMessageStore
{
    /// <summary>
    /// Atomically records <paramref name="messageId"/> as processed. Returns <c>true</c> when it was newly recorded
    /// (the caller should proceed) and <c>false</c> when it had already been processed (the caller should skip).
    /// </summary>
    Task<bool> TryMarkProcessedAsync(string messageId, CancellationToken cancellationToken);
}
