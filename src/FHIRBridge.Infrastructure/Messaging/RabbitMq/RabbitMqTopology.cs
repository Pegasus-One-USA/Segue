using FHIRBridge.Application.Messaging;
using RabbitMQ.Client;

namespace FHIRBridge.Infrastructure.Messaging.RabbitMq;

/// <summary>Resolves queue names per command type and declares durable queues with a dead-letter companion.</summary>
internal static class RabbitMqTopology
{
    public static string ResolveQueue(Type messageType, RabbitMqOptions options)
    {
        if (messageType == typeof(PipelineRunCommand))
        {
            return options.PipelineRunsQueue;
        }

        if (messageType == typeof(WebhookIngestionCommand))
        {
            return options.WebhookIngestionQueue;
        }

        throw new NotSupportedException($"No RabbitMQ queue is mapped for message type '{messageType.Name}'.");
    }

    /// <summary>
    /// Declares the durable work queue plus a <c>{queue}.dlq</c> dead-letter queue. Poison/unhandled messages are
    /// dead-lettered (nack with requeue:false) so they don't spin forever.
    /// </summary>
    public static async Task DeclareAsync(IChannel channel, string queue, CancellationToken cancellationToken)
    {
        var deadLetterQueue = $"{queue}.dlq";

        await channel.QueueDeclareAsync(
            queue: deadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        var arguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = deadLetterQueue
        };

        await channel.QueueDeclareAsync(
            queue: queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments,
            cancellationToken: cancellationToken);
    }
}
