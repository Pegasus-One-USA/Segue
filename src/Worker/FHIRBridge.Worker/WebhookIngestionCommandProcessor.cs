using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Governance;

namespace FHIRBridge.Worker;

/// <summary>
/// Processor worker role for webhook ingestion: consumes <see cref="WebhookIngestionCommand"/> messages and runs each
/// via the scoped <see cref="IWebhookIngestionCommandHandler"/>. Intended to be active when the webhook API enqueues
/// asynchronously over a shared transport (RabbitMQ / Azure Service Bus). With the in-process transport, the API and
/// Worker would need to run in the same process for the queue to be shared.
/// </summary>
/// <remarks>
/// <b>Live (2026-07-18 migration).</b> Registered in <c>Program.cs</c>, closing a previously-silent gap:
/// <c>WebhookIngestionController</c> (Api host) enqueues a <see cref="WebhookIngestionCommand"/> via
/// <c>IWebhookIngestionDispatcher</c> on every inbound webhook call; before this, nothing consumed it in any
/// transport configuration, so a webhook-triggered run was accepted (200/202) and then silently never executed.
/// Note the in-memory transport still can't cross the Api↔Worker process boundary — this consumer only sees
/// messages end-to-end when <c>Messaging:Provider</c> is RabbitMQ or Azure Service Bus.
/// </remarks>
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

        try
        {
            await handler.HandleAsync(command, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var governanceLogger = scope.ServiceProvider.GetRequiredService<IGovernanceLogger>();
            await governanceLogger.LogErrorAsync(
                new ErrorEntry(
                    "Error",
                    exception.GetType().Name,
                    exception.Message,
                    exception.StackTrace,
                    "Worker",
                    command.CorrelationId),
                CancellationToken.None);
            throw;
        }
    }
}
