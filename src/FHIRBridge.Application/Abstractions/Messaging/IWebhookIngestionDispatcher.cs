using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>Publishes a <see cref="WebhookIngestionCommand"/> to the messaging transport.</summary>
public interface IWebhookIngestionDispatcher
{
    Task EnqueueAsync(WebhookIngestionCommand command, CancellationToken cancellationToken);
}
