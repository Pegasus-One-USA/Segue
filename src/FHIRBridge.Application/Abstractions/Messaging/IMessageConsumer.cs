namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>
/// Provider-agnostic consume loop. Implementations deliver each message to <paramref name="handler"/> until the
/// supplied token is cancelled. Acknowledgement / retry semantics are the implementation's concern.
/// </summary>
public interface IMessageConsumer<TMessage>
{
    Task StartAsync(Func<TMessage, CancellationToken, Task> handler, CancellationToken cancellationToken);
}
