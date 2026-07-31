namespace FHIRBridge.Worker;

/// <summary>
/// Options for the schedule dispatcher worker role. Enabled by default (2026-07-18 scheduling migration) — this
/// is now the live scheduler: it periodically claims due scheduled runs (atomically, via
/// IScheduleEvaluationService.ClaimDueRunsAsync) and enqueues pipeline-run commands for PipelineRunCommandProcessor
/// to consume. In Azure this role maps to a scheduled Container Apps Job.
/// </summary>
public sealed class ScheduleDispatcherOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Verbose per-tick heartbeat and per-route due/skip diagnostic logging, toggled live via the
    /// <c>ScheduleDispatcher:HeartbeatLoggingEnabled</c> system setting (no restart required). Meant to be turned on
    /// while diagnosing "scheduled workflow didn't fire" issues and back off once confirmed healthy, since it logs
    /// every dispatcher tick and every configured route on every tick.
    /// </summary>
    public bool HeartbeatLoggingEnabled { get; set; } = true;
}
