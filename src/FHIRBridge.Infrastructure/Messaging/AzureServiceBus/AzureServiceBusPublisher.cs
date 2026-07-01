using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace FHIRBridge.Infrastructure.Messaging.AzureServiceBus;

/// <summary>Publishes JSON command messages to Service Bus queues, caching one sender per queue.</summary>
public sealed class AzureServiceBusPublisher : IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    public AzureServiceBusPublisher(ServiceBusClient client)
    {
        _client = client;
    }

    public async Task PublishAsync(string queue, string messageId, object message, CancellationToken cancellationToken)
    {
        var sender = _senders.GetOrAdd(queue, name => _client.CreateSender(name));
        var body = BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(message, message.GetType()));

        var serviceBusMessage = new ServiceBusMessage(body)
        {
            MessageId = messageId,          // enables native duplicate detection when the queue is configured for it
            ContentType = "application/json"
        };

        await sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync();
        }
    }
}
