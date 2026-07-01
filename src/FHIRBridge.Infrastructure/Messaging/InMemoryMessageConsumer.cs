using FHIRBridge.Application.Abstractions.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Messaging;

public sealed class InMemoryMessageConsumer<TMessage> : IMessageConsumer<TMessage>
{
    private readonly InMemoryMessageChannel<TMessage> _channel;
    private readonly ILogger<InMemoryMessageConsumer<TMessage>> _logger;

    public InMemoryMessageConsumer(
        InMemoryMessageChannel<TMessage> channel,
        ILogger<InMemoryMessageConsumer<TMessage>>? logger = null)
    {
        _channel = channel;
        _logger = logger ?? NullLogger<InMemoryMessageConsumer<TMessage>>.Instance;
    }

    public async Task StartAsync(
        Func<TMessage, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await handler(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The in-process transport has no dead-letter queue; log and keep consuming so the loop survives.
                _logger.LogError(exception, "In-memory handler for {MessageType} failed; message dropped.", typeof(TMessage).Name);
            }
        }
    }
}
