using System.Text.Json;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FHIRBridge.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// Consumes a durable RabbitMQ queue for <typeparamref name="TMessage"/> and dispatches each message to the handler.
/// Acks on success; dead-letters (nack, requeue:false) poison messages or handler failures. Runs until cancelled.
/// </summary>
public sealed class RabbitMqMessageConsumer<TMessage> : IMessageConsumer<TMessage>
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqMessageConsumer<TMessage>> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public RabbitMqMessageConsumer(
        RabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqMessageConsumer<TMessage>> logger,
        IServiceScopeFactory scopeFactory)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    // Deserialization failures happen before PipelineRunCommandHandler's own IGlobalExceptionManager capture ever
    // runs (see PipelineRunCommandProcessor.HandleAsync), so without this, a poison message vanishes into a
    // dead-letter queue with only an ILogger/Seq trace — invisible in production, where there is no Seq. A fresh
    // scope is required since this consumer is a long-lived singleton but IGlobalExceptionManager (→
    // IGovernanceLogger → FHIRBridgeDbContext) is scoped.
    private async Task CaptureDeserializationFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
        await exceptionManager.CaptureAsync(
            exception,
            new ExceptionContext(Module: "Message Deserialization"),
            cancellationToken);
    }

    public async Task StartAsync(Func<TMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        var queue = RabbitMqTopology.ResolveQueue(typeof(TMessage), _options);
        var connection = await _connection.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await RabbitMqTopology.DeclareAsync(channel, queue, cancellationToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, deliver) =>
        {
            TMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<TMessage>(deliver.Body.Span);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to deserialize {MessageType}; dead-lettering.", typeof(TMessage).Name);
                await CaptureDeserializationFailureAsync(exception, CancellationToken.None);
                await channel.BasicNackAsync(deliver.DeliveryTag, multiple: false, requeue: false, cancellationToken);
                return;
            }

            if (message is null)
            {
                await channel.BasicNackAsync(deliver.DeliveryTag, multiple: false, requeue: false, cancellationToken);
                return;
            }

            try
            {
                await handler(message, cancellationToken);
                await channel.BasicAckAsync(deliver.DeliveryTag, multiple: false, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Handler failed for {MessageType}; dead-lettering.", typeof(TMessage).Name);
                await channel.BasicNackAsync(deliver.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            }
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken);

        var completion = new TaskCompletionSource();
        using var registration = cancellationToken.Register(() => completion.TrySetResult());
        await completion.Task;

        await channel.CloseAsync(CancellationToken.None);
    }
}
