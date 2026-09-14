namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>
/// Execution-history counts that ride alongside <see cref="LicenseUsageCounts"/> in the usage ledger and the
/// heartbeat check-in payload, but aren't part of that interface: they need direct access to execution-history
/// tables (<c>ConfiguredPipelineRunRecord</c>, Runtime-plane <c>WorkflowRun</c>,
/// <c>PipelineRunResourceRecord</c>) that no existing repository exposes counts for. <see cref="ProcessedRecordsThisMonth"/>
/// and <see cref="SuccessfulExecutionsThisMonth"/> ARE read by <c>ILicenseQuotaGuard.EnsureCanStartNewRunAsync</c>
/// (each short-TTL cached there, not re-queried per call) to enforce their matching <c>LicenseLimits</c> caps;
/// the two cumulative all-time counts remain purely informational.
/// </summary>
public interface ILicenseUsageExecutionStatsProvider
{
    Task<LicenseUsageExecutionStats> GetCurrentStatsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="CumulativeConfiguredPipelineRunCount"/>/<see cref="CumulativeRuntimeWorkflowRunCount"/> are
/// all-time counts across both pipeline-execution planes (mirroring how <c>LicenseUsageCounts.WorkflowCount</c>
/// combines the same two planes for configured "workflow" totals) — purely informational, nothing gates on
/// these two. <see cref="ProcessedRecordsThisMonth"/> is a count of resources fetched within the current
/// calendar month (UTC). <see cref="SuccessfulExecutionsThisMonth"/> combines both planes' SUCCESSFUL run
/// counts (Configured Pipeline <c>Status == "Completed"</c>, matching the existing success-filter convention in
/// <c>ConfiguredPipelineService</c>; Runtime plane <c>WorkflowRunStatus.Succeeded</c> only — deliberately NOT
/// <c>PartialSuccess</c>, since that status means at least one resource type was skipped) within the current
/// calendar month (UTC), keyed off each run's completion timestamp.
/// </summary>
public sealed record LicenseUsageExecutionStats(
    long CumulativeConfiguredPipelineRunCount,
    long CumulativeRuntimeWorkflowRunCount,
    long ProcessedRecordsThisMonth,
    long SuccessfulExecutionsThisMonth);
