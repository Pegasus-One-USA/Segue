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

    // HIPAA #14: bound DLQ retention so poison/PHI-carrying messages don't accumulate unbounded — aligned to a
    // 30-day incident-response SLA. NOTE: RabbitMQ rejects redeclaring an existing queue with different arguments
    // (PRECONDITION_FAILED) — an already-deployed `.dlq` queue must be deleted (after triage) or the app queue
    // renamed once, before this takes effect in that environment.
    private static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(30);
    private const int DeadLetterMaxLength = 100_000;

    /// <summary>
    /// Declares the durable work queue plus a <c>{queue}.dlq</c> dead-letter queue. Poison/unhandled messages are
    /// dead-lettered (nack with requeue:false) so they don't spin forever.
    /// </summary>
    public static async Task DeclareAsync(IChannel channel, string queue, CancellationToken cancellationToken)
    {
        var deadLetterQueue = $"{queue}.dlq";

        var deadLetterArguments = new Dictionary<string, object?>
        {
            ["x-message-ttl"] = (int)DeadLetterRetention.TotalMilliseconds,
            ["x-max-length"] = DeadLetterMaxLength
        };

        await channel.QueueDeclareAsync(
            queue: deadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: deadLetterArguments,
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
