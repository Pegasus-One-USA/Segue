using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FHIRBridge.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// Publishes JSON command messages to durable RabbitMQ queues. Holds one channel (guarded, since channels are not
/// safe for concurrent use) and declares each queue + dead-letter queue once.
/// </summary>
public sealed class RabbitMqPublisher : IAsyncDisposable
{
    private readonly RabbitMqConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _declaredQueues = new(StringComparer.Ordinal);
    private IChannel? _channel;

    public RabbitMqPublisher(RabbitMqConnection connection)
    {
        _connection = connection;
    }

    public async Task PublishAsync(string queue, string messageId, object message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType());

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is not { IsOpen: true })
            {
                var connection = await _connection.GetConnectionAsync(cancellationToken);
                _channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            }

            if (_declaredQueues.Add(queue))
            {
                await RabbitMqTopology.DeclareAsync(_channel, queue, cancellationToken);
            }

            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = messageId,
                ContentType = "application/json"
            };

            await _channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: queue,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }
}
