using System.Threading.Channels;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// An unbounded in-process channel that backs the InMemory messaging provider. Registered as a singleton per
/// closed message type so the dispatcher and consumer share the same queue. Used for local development and tests;
/// production swaps in RabbitMQ (Phase 3) or Azure Service Bus (Phase 4) behind the same abstractions.
/// </summary>
public sealed class InMemoryMessageChannel<TMessage>
{
    private readonly Channel<TMessage> _channel =
        Channel.CreateUnbounded<TMessage>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    public ChannelWriter<TMessage> Writer => _channel.Writer;

    public ChannelReader<TMessage> Reader => _channel.Reader;
}
