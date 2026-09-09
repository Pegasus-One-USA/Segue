using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;

namespace FHIRBridge.Worker;

/// <summary>
/// Processor worker role: consumes <see cref="PipelineRunCommand"/> messages from the transport and runs each via
/// the scoped <see cref="IPipelineRunCommandHandler"/>. Always on — it idles until commands arrive. In Azure this
/// role maps to an event-triggered Container Apps Job scaled by queue depth (Phase 4).
/// </summary>
/// <remarks>
/// <b>Live (2026-07-18 migration).</b> Registered in <c>Program.cs</c>. Consumes <see cref="PipelineRunCommand"/>
/// messages enqueued by <c>ScheduleDispatcher</c> (the now-live scheduler — see its remarks) — its
/// <c>RetryHistory</c>/<c>ErrorLog</c> instrumentation is genuinely exercised now, not just correct-but-unreachable.
/// </remarks>
public sealed class PipelineRunCommandProcessor : BackgroundService
{
    private readonly IMessageConsumer<PipelineRunCommand> _consumer;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PipelineRunCommandProcessor> _logger;

    public PipelineRunCommandProcessor(
        IMessageConsumer<PipelineRunCommand> consumer,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PipelineRunCommandProcessor> logger)
    {
        _consumer = consumer;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            LogEvents.MessageProcessorStarted,
            "Pipeline run command processor started; waiting for scheduled run commands.");
        return _consumer.StartAsync(HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(PipelineRunCommand command, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IPipelineRunCommandHandler>();

        // This is the DEFAULT route-scheduling path (RuntimeWorkerOptions.DirectRouteSchedulingEnabled is off), so
        // it carries most scheduled route runs — yet until now it logged only at startup. Everything the handler
        // and the configured pipeline write beneath this inherits the message identity from the scope.
        using var commandLogScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = command.MessageId,
            ["CorrelationId"] = command.CorrelationId,
        });

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _logger.LogInformation(
            LogEvents.MessageConsumed,
            "Consuming pipeline run command {MessageId}: ResourceTypes=[{ResourceTypes}] RouteIds=[{RouteIds}] "
            + "RunDueSchedulesOnly={RunDueSchedulesOnly} UseBulkExport={UseBulkExport} "
            + "ScheduledAtUtc={ScheduledAtUtc} TriggeredBy={TriggeredBy}",
            command.MessageId, string.Join(", ", command.ResourceTypes),
            command.RouteIds is null ? "all" : string.Join(", ", command.RouteIds),
            command.RunDueSchedulesOnly, command.UseBulkExport, command.ScheduledAtUtc, command.TriggeredBy);

        try
        {
            await handler.HandleAsync(command, cancellationToken);

            _logger.LogInformation(
                LogEvents.MessageCompleted,
                "Pipeline run command {MessageId} completed in {ElapsedMs}ms.",
                command.MessageId,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Centralized capture for the Worker host — rethrow so the transport's own
            // retry/dead-letter behavior (see MessageRetry / IMessageConsumer) is unaffected.
            var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await exceptionManager.CaptureAsync(
                exception,
                new ExceptionContext(Module: "Pipeline Run", CorrelationId: command.CorrelationId),
                CancellationToken.None);

            // Rethrowing hands this to the transport's retry/dead-letter machinery, so expect this event to repeat
            // for the same MessageId across attempts — that repetition is the retry, not duplicate work.
            _logger.LogError(
                LogEvents.MessageFailed,
                exception,
                "Pipeline run command {MessageId} failed after {ElapsedMs}ms and is being handed back to the "
                + "transport for retry/dead-lettering: {FailureReason}",
                command.MessageId,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.Message);
            throw;
        }
    }
}
