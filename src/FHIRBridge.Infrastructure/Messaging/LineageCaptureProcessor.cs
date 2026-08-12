using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// Consumes <see cref="LineageCaptureCommand"/> messages published by the transform executor
/// (<c>MappingNodeExecutor</c>) and persists each batch's field-lineage hops via the scoped
/// <see cref="ILineageCaptureCommandHandler"/>. This is where the actual <c>FieldLineageEntries</c> DB write
/// happens — entirely out of the pipeline's own execution path, so a slow or backlogged consumer never adds
/// latency to a transform run.
/// </summary>
/// <remarks>
/// Registered as a hosted service in BOTH the Api and Worker hosts (not just Worker, unlike
/// <c>PipelineRunCommandProcessor</c>). A manual/interactive workflow run (<c>POST /workflows/{id}/run</c>)
/// executes the whole pipeline — including the lineage publish call — synchronously inside the Api process, not
/// the Worker. With the InMemory messaging provider, <c>InMemoryMessageChannel&lt;T&gt;</c> is an in-process-only
/// <see cref="System.Threading.Channels.Channel{T}"/> — a message enqueued in the Api process's channel is never
/// visible to a consumer running only in the separate Worker process. Running this consumer in both hosts closes
/// that gap; with a real broker (RabbitMQ/Azure Service Bus) both instances just compete for messages off the
/// same durable queue, which is safe since <see cref="LineageCaptureCommandHandler"/> dedupes by
/// <see cref="LineageCaptureCommand.MessageId"/> via <see cref="IProcessedMessageStore"/>.
/// </remarks>
public sealed class LineageCaptureProcessor : BackgroundService
{
    private readonly IMessageConsumer<LineageCaptureCommand> _consumer;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<LineageCaptureProcessor> _logger;

    public LineageCaptureProcessor(
        IMessageConsumer<LineageCaptureCommand> consumer,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<LineageCaptureProcessor> logger)
    {
        _consumer = consumer;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Lineage capture processor started; waiting for lineage capture commands.");
        return _consumer.StartAsync(HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(LineageCaptureCommand command, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ILineageCaptureCommandHandler>();

        try
        {
            await handler.HandleAsync(command, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Lineage is diagnostic/audit data, not correctness-critical — capture the exception for visibility
            // but never rethrow into the transport's retry/dead-letter path, unlike PipelineRunCommandProcessor.
            // A permanently-broken lineage write must not pile up as poison messages against a live queue.
            var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await exceptionManager.CaptureAsync(
                exception,
                new ExceptionContext(Module: "Lineage Capture", CorrelationId: command.MessageId),
                CancellationToken.None);
        }
    }
}
