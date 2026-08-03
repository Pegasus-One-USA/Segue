using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Scheduling;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Governance;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Scheduling;

/// <summary>
/// Claims due scheduled runs and publishes a <see cref="PipelineRunCommand"/> for each. The command's MessageId is
/// deterministic per claimed route set and minute slot, so duplicate evaluations in the same minute dedupe downstream.
/// </summary>
/// <remarks>
/// <b>Live scheduler (2026-07-18 migration).</b> <c>ScheduleDispatcherWorker</c> is now registered in
/// <c>Worker/Program.cs</c> and enabled by default (<see cref="Application.Abstractions.Scheduling.IScheduleEvaluationService.ClaimDueRunsAsync"/>
/// is the atomic claim, verified safe under concurrent Worker instances via <c>ResourcePipelineRoute</c>'s
/// RowVersion optimistic-concurrency token — a losing instance gets <c>DbUpdateConcurrencyException</c>, caught
/// and logged per-tick, never a duplicate dispatch). <c>Worker.RunDueRoutesAsync</c> (direct-call polling) is now
/// off by default — see <c>RuntimeWorkerOptions.DirectRouteSchedulingEnabled</c> — kept only as a fast-rollback
/// switch; the Worker host fails fast at startup if both paths are ever enabled at once, since they use unrelated
/// due-detection logic with no protection against double-processing between them. See
/// docs/backend/08-governance-logging-status.md for the full history.
/// </remarks>
public sealed class ScheduleDispatcher : IScheduleDispatcher
{
    private readonly IScheduleEvaluationService _evaluationService;
    private readonly IPipelineRunDispatcher _pipelineRunDispatcher;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ILogger<ScheduleDispatcher> _logger;

    public ScheduleDispatcher(
        IScheduleEvaluationService evaluationService,
        IPipelineRunDispatcher pipelineRunDispatcher,
        IGovernanceLogger governanceLogger,
        ILogger<ScheduleDispatcher> logger)
    {
        _evaluationService = evaluationService;
        _pipelineRunDispatcher = pipelineRunDispatcher;
        _governanceLogger = governanceLogger;
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

            var schedulerLabel = dueRun.RouteLabels.Count > 0
                ? $"Scheduler ({string.Join(", ", dueRun.RouteLabels)})"
                : $"Scheduler ({routeKey})";

            await _governanceLogger.LogSchedulerRunAsync(
                new SchedulerRunEntry(schedulerLabel, "Dispatched", dueRun.RouteIds.Count, messageId),
                cancellationToken);
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
