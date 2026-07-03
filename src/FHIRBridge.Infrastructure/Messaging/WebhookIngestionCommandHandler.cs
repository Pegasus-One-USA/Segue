using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// Runs a queued webhook ingestion. Deduplicates by MessageId, retries transient failures with backoff, and throws
/// when retries are exhausted or the run reports a Failed status — so the transport dead-letters the message.
/// </summary>
public sealed class WebhookIngestionCommandHandler : IWebhookIngestionCommandHandler
{
    private readonly IConfiguredPipelineService _pipelineService;
    private readonly IProcessedMessageStore _processedMessageStore;
    private readonly MessageProcessingOptions _options;
    private readonly ILogger<WebhookIngestionCommandHandler> _logger;

    public WebhookIngestionCommandHandler(
        IConfiguredPipelineService pipelineService,
        IProcessedMessageStore processedMessageStore,
        IOptions<MessageProcessingOptions> options,
        ILogger<WebhookIngestionCommandHandler> logger)
    {
        _pipelineService = pipelineService;
        _processedMessageStore = processedMessageStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(WebhookIngestionCommand command, CancellationToken cancellationToken)
    {
        if (!await _processedMessageStore.TryMarkProcessedAsync(command.MessageId, cancellationToken))
        {
            _logger.LogInformation("Skipping already-processed webhook command {MessageId}.", command.MessageId);
            return;
        }

        ConfiguredPipelineRunDto? run = null;

        await MessageRetry.ExecuteAsync(
            async token => run = await _pipelineService.StartWebhookAsync(
                command.WebhookConfigurationId,
                new WebhookIngestionRequest(command.ResourceJson, command.TriggeredBy, command.CorrelationId),
                token),
            _options,
            _logger,
            $"Webhook command {command.MessageId}",
            cancellationToken);

        if (run is not null && string.Equals(run.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Webhook run {run.Id} failed: {string.Join("; ", run.Errors)}");
        }

        _logger.LogInformation(
            "Webhook run {RunId} finished with status {Status} ({Written} record(s) written).",
            run?.Id, run?.Status, run?.WrittenRecordCount);
    }
}
