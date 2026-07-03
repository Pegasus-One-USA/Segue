using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Scheduling;
using FHIRBridge.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Scheduling;

/// <summary>
/// Claims due scheduled runs and publishes a <see cref="PipelineRunCommand"/> for each. The command's MessageId is
/// deterministic per claimed route set and minute slot, so duplicate evaluations in the same minute dedupe downstream.
/// </summary>
public sealed class ScheduleDispatcher : IScheduleDispatcher
{
    private readonly IScheduleEvaluationService _evaluationService;
    private readonly IPipelineRunDispatcher _pipelineRunDispatcher;
    private readonly ILogger<ScheduleDispatcher> _logger;

    public ScheduleDispatcher(
        IScheduleEvaluationService evaluationService,
        IPipelineRunDispatcher pipelineRunDispatcher,
        ILogger<ScheduleDispatcher> logger)
    {
        _evaluationService = evaluationService;
        _pipelineRunDispatcher = pipelineRunDispatcher;
        _logger = logger;
    }

    public async Task<int> DispatchDueRunsAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var dueRuns = await _evaluationService.ClaimDueRunsAsync(utcNow, cancellationToken);
        var slotUtc = TruncateToMinuteUtc(utcNow);

        foreach (var dueRun in dueRuns)
        {
            var routeKey = string.Join(",", dueRun.RouteIds.OrderBy(id => id).Select(id => id.ToString("N")));
            var messageId = $"sched:{routeKey}:{slotUtc:yyyyMMddHHmm}";
            var command = new PipelineRunCommand(
                dueRun.ResourceTypes,
                RunDueSchedulesOnly: true,
                slotUtc,
                "scheduler",
                CorrelationId: messageId,
                MessageId: messageId)
            {
                RouteIds = dueRun.RouteIds
            };

            await _pipelineRunDispatcher.EnqueueAsync(command, cancellationToken);
        }

        if (dueRuns.Count > 0)
        {
            _logger.LogInformation("Dispatched {DueRunCount} scheduled pipeline run command(s).", dueRuns.Count);
        }

        return dueRuns.Count;
    }

    private static DateTime TruncateToMinuteUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
    }
}
