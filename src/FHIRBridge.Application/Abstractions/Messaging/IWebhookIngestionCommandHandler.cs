using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>
/// Executes a <see cref="WebhookIngestionCommand"/> consumed from the messaging transport: enforces idempotency,
/// then runs the webhook pipeline. Kept separate from the hosting background service so it is unit-testable.
/// </summary>
public interface IWebhookIngestionCommandHandler
{
    Task HandleAsync(WebhookIngestionCommand command, CancellationToken cancellationToken);
}
