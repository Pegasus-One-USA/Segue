using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Infrastructure.Messaging;

public sealed class InMemoryWebhookIngestionDispatcher : IWebhookIngestionDispatcher
{
    private readonly InMemoryMessageChannel<WebhookIngestionCommand> _channel;

    public InMemoryWebhookIngestionDispatcher(InMemoryMessageChannel<WebhookIngestionCommand> channel)
    {
        _channel = channel;
    }

    public async Task EnqueueAsync(WebhookIngestionCommand command, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(command, cancellationToken);
    }
}
