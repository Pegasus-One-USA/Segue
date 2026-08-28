using Azure.Messaging.ServiceBus;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging.AzureServiceBus;

/// <summary>
/// Consumes a Service Bus queue for <typeparamref name="TMessage"/> via a <see cref="ServiceBusProcessor"/>.
/// Completes on success; dead-letters poison messages and handler failures. Runs until cancelled.
/// </summary>
public sealed class AzureServiceBusMessageConsumer<TMessage> : IMessageConsumer<TMessage>
{
    private readonly ServiceBusClient _client;
    private readonly AzureServiceBusOptions _options;
    private readonly ILogger<AzureServiceBusMessageConsumer<TMessage>> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public AzureServiceBusMessageConsumer(
        ServiceBusClient client,
        IOptions<AzureServiceBusOptions> options,
        ILogger<AzureServiceBusMessageConsumer<TMessage>> logger,
        IServiceScopeFactory scopeFactory)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    // See RabbitMqMessageConsumer's identical helper — a poison message's deserialization failure otherwise leaves
    // only an ILogger/Seq trace before it's dead-lettered, invisible in production without Seq.
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
        var queue = ResolveQueue(typeof(TMessage), _options);
        await using var processor = _client.CreateProcessor(queue, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = _options.MaxConcurrentCalls,
            AutoCompleteMessages = false
        });

        processor.ProcessMessageAsync += async args =>
        {
            TMessage? message;
            try
            {
                message = args.Message.Body.ToObjectFromJson<TMessage>();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to deserialize {MessageType}; dead-lettering.", typeof(TMessage).Name);
                await CaptureDeserializationFailureAsync(exception, CancellationToken.None);
                await args.DeadLetterMessageAsync(args.Message, "DeserializationError", exception.Message, args.CancellationToken);
                return;
            }

            if (message is null)
            {
                await args.DeadLetterMessageAsync(args.Message, "NullMessage", "Message deserialized to null.", args.CancellationToken);
                return;
            }

            try
            {
                await handler(message, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Handler failed for {MessageType}; dead-lettering.", typeof(TMessage).Name);
                await args.DeadLetterMessageAsync(args.Message, "HandlerFailed", exception.Message, args.CancellationToken);
            }
        };

        processor.ProcessErrorAsync += errorArgs =>
        {
            _logger.LogError(errorArgs.Exception, "Service Bus processor error on {Entity} ({Source}).", errorArgs.EntityPath, errorArgs.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(cancellationToken);

        var completion = new TaskCompletionSource();
        using var registration = cancellationToken.Register(() => completion.TrySetResult());
        await completion.Task;

        await processor.StopProcessingAsync(CancellationToken.None);
    }

    private static string ResolveQueue(Type messageType, AzureServiceBusOptions options)
    {
        if (messageType == typeof(PipelineRunCommand))
        {
            return options.PipelineRunsQueue;
        }

        if (messageType == typeof(WebhookIngestionCommand))
        {
            return options.WebhookIngestionQueue;
        }

        throw new NotSupportedException($"No Service Bus queue is mapped for message type '{messageType.Name}'.");
    }
}
