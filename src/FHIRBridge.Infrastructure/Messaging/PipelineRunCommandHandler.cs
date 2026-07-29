using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// Runs a claimed scheduled pipeline run. Deduplicates by MessageId, retries transient failures with backoff, and
/// throws when retries are exhausted or the run reports a Failed status — so the transport dead-letters the message.
/// </summary>
public sealed class PipelineRunCommandHandler : IPipelineRunCommandHandler
{
    private readonly IConfiguredPipelineService _pipelineService;
    private readonly IProcessedMessageStore _processedMessageStore;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly IAmbientActorContext _ambientActorContext;
    private readonly MessageProcessingOptions _options;
    private readonly ILogger<PipelineRunCommandHandler> _logger;

    public PipelineRunCommandHandler(
        IConfiguredPipelineService pipelineService,
        IProcessedMessageStore processedMessageStore,
        IGovernanceLogger governanceLogger,
        IAmbientActorContext ambientActorContext,
        IOptions<MessageProcessingOptions> options,
        ILogger<PipelineRunCommandHandler> logger)
    {
        _pipelineService = pipelineService;
        _processedMessageStore = processedMessageStore;
        _governanceLogger = governanceLogger;
        _ambientActorContext = ambientActorContext;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(PipelineRunCommand command, CancellationToken cancellationToken)
    {
        if (!await _processedMessageStore.TryMarkProcessedAsync(command.MessageId, cancellationToken))
        {
            _logger.LogInformation("Skipping already-processed pipeline run command {MessageId}.", command.MessageId);
            return;
        }

        ConfiguredPipelineRunDto? run = null;

        var actorLabel = string.Equals(command.TriggeredBy, "scheduler", StringComparison.OrdinalIgnoreCase)
            ? "Scheduler (Automated Pipeline Run)"
            : $"Automated Pipeline Run ({command.TriggeredBy ?? "unknown trigger"})";
        using var actorScope = _ambientActorContext.BeginScope(actorLabel, command.CorrelationId);

        // Transient failures (e.g. source/DB unavailable) are retried with backoff.
        await MessageRetry.ExecuteAsync(
            async token => run = await _pipelineService.StartAsync(
                new StartConfiguredPipelineRunRequest(command.ResourceTypes, command.TriggeredBy, command.CorrelationId)
                {
                    RunDueSchedulesOnly = command.RunDueSchedulesOnly,
                    ScheduledAtUtc = command.ScheduledAtUtc,
                    RouteIds = command.RouteIds,
                    UseBulkExport = command.UseBulkExport
                },
                token),
            _options,
            _logger,
            $"Pipeline run command {command.MessageId}",
            cancellationToken,
            onRetryAsync: (attempt, delayMs, exception, token) => _governanceLogger.LogRetryAsync(
                new RetryEntry(
                    $"Pipeline run command {command.MessageId}",
                    attempt,
                    delayMs,
                    exception.Message,
                    command.CorrelationId),
                token));

        // A run that completed but reported Failed is dead-lettered (no point retrying a deterministic failure).
        if (run is not null && string.Equals(run.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Pipeline run {run.Id} failed: {string.Join("; ", run.Errors)}");
        }

        _logger.LogInformation(
            "Pipeline run {RunId} finished with status {Status} ({Written} record(s) written).",
            run?.Id, run?.Status, run?.WrittenRecordCount);
    }
}
