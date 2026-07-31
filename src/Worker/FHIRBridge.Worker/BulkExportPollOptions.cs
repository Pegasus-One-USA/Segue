namespace FHIRBridge.Worker;

/// <summary>
/// Options for the bulk-export poll worker role. Enabled by default: it periodically checks the status of every
/// in-flight <c>BulkExportJob</c> (one GET per job per tick) instead of any caller blocking for the job's full
/// duration inline.
/// </summary>
public sealed class BulkExportPollOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Worker tick cadence — how often the whole batch of due jobs is checked.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Maximum jobs claimed per tick.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Fallback wait before re-polling a job whose server response carried no Retry-After hint.</summary>
    public int DefaultPollIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum status polls before a job is given up on and marked Failed.</summary>
    public int MaxPollAttempts { get; set; } = 120;
}
