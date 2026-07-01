namespace FHIRBridge.Worker;

/// <summary>
/// Options for the schedule dispatcher worker role. Disabled by default; when enabled it periodically claims due
/// scheduled runs and enqueues pipeline-run commands. In Azure this role maps to a scheduled Container Apps Job.
/// </summary>
public sealed class ScheduleDispatcherOptions
{
    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 60;
}
