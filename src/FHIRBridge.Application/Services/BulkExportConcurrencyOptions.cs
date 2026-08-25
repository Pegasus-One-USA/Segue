namespace FHIRBridge.Application.Services;

/// <summary>
/// Generic (vendor-agnostic) cap on simultaneously in-flight FHIR Bulk Data <c>$export</c> jobs per SourceConnection.
/// Every Bulk Data server publishes its own concurrency limits enforced server-side (e.g. athenahealth: 2/practice
/// Preview, 5/practice Production; Epic publishes its own, different numbers) — this is FHIRBridge's own
/// client-side guard so a caller finds out it's at capacity via a fast, cheap local check instead of only ever
/// discovering the cap via a reactive 429 from the source server. Deliberately not hardcoded to any one vendor's
/// numbers — configure per-deployment ("BulkExport:MaxConcurrentJobsPerSourceConnection") to match whichever
/// server(s) are in play, consistent with the registry/strategy (no vendor-conditional switch) convention: adding a
/// vendor-specific value later is a configuration change, not a code branch.
/// </summary>
public sealed class BulkExportConcurrencyOptions
{
    public int MaxConcurrentJobsPerSourceConnection { get; set; } = 2;

    /// <summary>Retry-After hint (seconds) surfaced to the caller when the limit is hit.</summary>
    public int RetryAfterSeconds { get; set; } = 30;
}
