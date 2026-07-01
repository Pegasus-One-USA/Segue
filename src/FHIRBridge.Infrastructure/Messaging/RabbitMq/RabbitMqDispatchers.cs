using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging.RabbitMq;

public sealed class RabbitMqPipelineRunDispatcher : IPipelineRunDispatcher
{
    private readonly RabbitMqPublisher _publisher;
    private readonly RabbitMqOptions _options;

    public RabbitMqPipelineRunDispatcher(RabbitMqPublisher publisher, IOptions<RabbitMqOptions> options)
    {
        _publisher = publisher;
        _options = options.Value;
    }

    public Task EnqueueAsync(PipelineRunCommand command, CancellationToken cancellationToken)
    {
        return _publisher.PublishAsync(_options.PipelineRunsQueue, command.MessageId, command, cancellationToken);
    }
}

public sealed class RabbitMqWebhookIngestionDispatcher : IWebhookIngestionDispatcher
{
    private readonly RabbitMqPublisher _publisher;
    private readonly RabbitMqOptions _options;

    public RabbitMqWebhookIngestionDispatcher(RabbitMqPublisher publisher, IOptions<RabbitMqOptions> options)
    {
        _publisher = publisher;
        _options = options.Value;
    }

    public Task EnqueueAsync(WebhookIngestionCommand command, CancellationToken cancellationToken)
    {
        return _publisher.PublishAsync(_options.WebhookIngestionQueue, command.MessageId, command, cancellationToken);
    }
}
