using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// DB-backed <see cref="ILicenseUsageExecutionStatsProvider"/>. <see cref="LicenseUsageExecutionStats.ProcessedRecordsThisMonth"/>
/// and <see cref="LicenseUsageExecutionStats.SuccessfulExecutionsThisMonth"/> are read (short-TTL cached) by
/// <c>ILicenseQuotaGuard.EnsureCanStartNewRunAsync</c> to enforce their matching caps; the two cumulative
/// counts stay purely informational.
/// </summary>
public sealed class EfLicenseUsageExecutionStatsProvider : ILicenseUsageExecutionStatsProvider
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfLicenseUsageExecutionStatsProvider(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<LicenseUsageExecutionStats> GetCurrentStatsAsync(CancellationToken cancellationToken)
    {
        var cumulativeConfiguredPipelineRunCount =
            await _dbContext.ConfiguredPipelineRuns.LongCountAsync(cancellationToken);
        var cumulativeRuntimeWorkflowRunCount =
            await _dbContext.WorkflowRuns.LongCountAsync(cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var startOfMonthUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var processedRecordsThisMonth = await _dbContext.PipelineRunResourceRecords
            .LongCountAsync(x => x.FetchedAtUtc >= startOfMonthUtc, cancellationToken);

        // "Completed" mirrors ConfiguredPipelineService's own existing success-filter convention (see its
        // GetRecentAsync-adjacent query). Runtime plane counts ONLY WorkflowRunStatus.Succeeded, deliberately
        // excluding PartialSuccess (a run where at least one resource type was skipped for authorization
        // reasons) — see this stats record's own remarks.
        var successfulConfiguredRunsThisMonth = await _dbContext.ConfiguredPipelineRuns
            .LongCountAsync(
                x => x.CompletedOnUtc >= startOfMonthUtc && x.Status == "Completed",
                cancellationToken);
        var successfulRuntimeRunsThisMonth = await _dbContext.WorkflowRuns
            .LongCountAsync(
                x => x.CompletedAt != null && x.CompletedAt >= startOfMonthUtc &&
                     x.Status == FHIRBridge.Runtime.Domain.Workflows.WorkflowRunStatus.Succeeded,
                cancellationToken);

        return new LicenseUsageExecutionStats(
            cumulativeConfiguredPipelineRunCount,
            cumulativeRuntimeWorkflowRunCount,
            processedRecordsThisMonth,
            successfulConfiguredRunsThisMonth + successfulRuntimeRunsThisMonth);
    }
}
