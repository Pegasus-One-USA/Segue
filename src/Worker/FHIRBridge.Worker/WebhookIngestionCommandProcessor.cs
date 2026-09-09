using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Observability.Logging;
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
        _logger.LogInformation(
            LogEvents.MessageProcessorStarted,
            "Webhook ingestion command processor started; waiting for webhook commands.");
        return _consumer.StartAsync(HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(WebhookIngestionCommand command, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IWebhookIngestionCommandHandler>();

        // WebhookIngestionCommand.ResourceJson is the raw inbound FHIR payload -- PHI. It is deliberately absent
        // from every message here; PayloadHash (the idempotency key) identifies the payload without carrying it.
        using var commandLogScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = command.MessageId,
            ["WebhookConfigurationId"] = command.WebhookConfigurationId,
            ["CorrelationId"] = command.CorrelationId,
        });

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _logger.LogInformation(
            LogEvents.MessageConsumed,
            "Consuming webhook ingestion command {MessageId} for webhook {WebhookConfigurationId} "
            + "(payload hash {PayloadHash}, triggered by {TriggeredBy}).",
            command.MessageId, command.WebhookConfigurationId, command.PayloadHash, command.TriggeredBy);

        try
        {
            await handler.HandleAsync(command, cancellationToken);

            _logger.LogInformation(
                LogEvents.MessageCompleted,
                "Webhook ingestion command {MessageId} completed in {ElapsedMs}ms.",
                command.MessageId,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await exceptionManager.CaptureAsync(
                exception,
                new ExceptionContext(Module: "Webhook Ingestion", CorrelationId: command.CorrelationId),
                CancellationToken.None);

            // Rethrown to the transport, so expect repeats of this event for the same MessageId across retries.
            _logger.LogError(
                LogEvents.MessageFailed,
                exception,
                "Webhook ingestion command {MessageId} for webhook {WebhookConfigurationId} failed after "
                + "{ElapsedMs}ms and is being handed back to the transport for retry/dead-lettering: {FailureReason}",
                command.MessageId, command.WebhookConfigurationId,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.Message);
            throw;
        }
    }
}
