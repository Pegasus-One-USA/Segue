using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Worker;

/// <summary>
/// Processor worker role for webhook ingestion: consumes <see cref="WebhookIngestionCommand"/> messages and runs each
/// via the scoped <see cref="IWebhookIngestionCommandHandler"/>. Active when the webhook API enqueues asynchronously
/// over a shared transport (RabbitMQ / Azure Service Bus). With the in-process transport, run the API and Worker in
/// the same process for the queue to be shared.
/// </summary>
public sealed class WebhookIngestionCommandProcessor : BackgroundService
{
    private readonly IMessageConsumer<WebhookIngestionCommand> _consumer;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<WebhookIngestionCommandProcessor> _logger;

    public WebhookIngestionCommandProcessor(
        IMessageConsumer<WebhookIngestionCommand> consumer,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<WebhookIngestionCommandProcessor> logger)
    {
        _consumer = consumer;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Webhook ingestion command processor started; waiting for webhook commands.");
        return _consumer.StartAsync(HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(WebhookIngestionCommand command, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IWebhookIngestionCommandHandler>();
        await handler.HandleAsync(command, cancellationToken);
    }
}
