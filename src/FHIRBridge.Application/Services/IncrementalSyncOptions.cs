namespace FHIRBridge.Application.Services;

/// <summary>
/// Controls automatic incremental ("since last run") source extraction. When enabled, a scheduled/manual search
/// pull appends <c>_lastUpdated=gt{watermark}</c> to the route's search parameters, where the watermark is the start
/// time of the most recent successfully completed run for the same resource type. Bound from the "IncrementalSync"
/// configuration section; defaults below apply when unbound. Skipped when the route already specifies
/// <c>_lastUpdated</c> (explicit wins) or when no prior completed run exists (first run does a full pull).
/// </summary>
public sealed class IncrementalSyncOptions
{
    public const string SectionName = "IncrementalSync";

    /// <summary>When true, scheduled/manual search pulls become incremental by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds subtracted from the watermark to tolerate clock skew / late-arriving updates. The small overlap is
    /// safe because writes are idempotent under Upsert. Default 60s.
    /// </summary>
    public int OverlapSeconds { get; set; } = 60;

    public static IncrementalSyncOptions Default { get; } = new();
}
