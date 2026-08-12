using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging.AzureServiceBus;

public sealed class AzureServiceBusPipelineRunDispatcher : IPipelineRunDispatcher
{
    private readonly AzureServiceBusPublisher _publisher;
    private readonly AzureServiceBusOptions _options;

    public AzureServiceBusPipelineRunDispatcher(AzureServiceBusPublisher publisher, IOptions<AzureServiceBusOptions> options)
    {
        _publisher = publisher;
        _options = options.Value;
    }

    public Task EnqueueAsync(PipelineRunCommand command, CancellationToken cancellationToken)
    {
        return _publisher.PublishAsync(_options.PipelineRunsQueue, command.MessageId, command, cancellationToken);
    }
}

public sealed class AzureServiceBusWebhookIngestionDispatcher : IWebhookIngestionDispatcher
{
    private readonly AzureServiceBusPublisher _publisher;
    private readonly AzureServiceBusOptions _options;

    public AzureServiceBusWebhookIngestionDispatcher(AzureServiceBusPublisher publisher, IOptions<AzureServiceBusOptions> options)
    {
        _publisher = publisher;
        _options = options.Value;
    }

    public Task EnqueueAsync(WebhookIngestionCommand command, CancellationToken cancellationToken)
    {
        return _publisher.PublishAsync(_options.WebhookIngestionQueue, command.MessageId, command, cancellationToken);
    }
}

public sealed class AzureServiceBusLineageCaptureDispatcher : ILineageCaptureDispatcher
{
    private readonly AzureServiceBusPublisher _publisher;
    private readonly AzureServiceBusOptions _options;

    public AzureServiceBusLineageCaptureDispatcher(AzureServiceBusPublisher publisher, IOptions<AzureServiceBusOptions> options)
    {
        _publisher = publisher;
        _options = options.Value;
    }

    public Task EnqueueAsync(LineageCaptureCommand command, CancellationToken cancellationToken)
    {
        return _publisher.PublishAsync(_options.LineageCaptureQueue, command.MessageId, command, cancellationToken);
    }
}
