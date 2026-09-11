using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Domain.Entities.Licensing;
using FHIRBridge.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Periodically appends one hash-chained <see cref="UsageLedgerEntry"/> capturing live usage counts — the
/// local, tamper-evident record behind "how much of the license is actually being used" and "how long has
/// this install been active". Purely local: reads existing counts providers and writes to the ledger table,
/// zero network dependency (contrast with <see cref="LicenseHeartbeatWorker"/>, which best-effort reports a
/// subset of the same numbers to a remote service and is designed to never block on that call failing).
/// Mirrors <c>AuditChainVerificationWorker</c>'s exact shape.
/// </summary>
public sealed class LicenseUsageSnapshotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<LicenseUsageSnapshotOptions> _options;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly ILogger<LicenseUsageSnapshotWorker> _logger;

    public LicenseUsageSnapshotWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<LicenseUsageSnapshotOptions> options,
        ISystemSettingsCache settingsCache,
        ILogger<LicenseUsageSnapshotWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _settingsCache = settingsCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var enabled = await _settingsCache.GetBoolAsync(
                "LicenseUsageSnapshot:Enabled", _options.Value.Enabled, stoppingToken);
            if (!enabled)
            {
                _logger.LogInformation(
                    "License usage snapshot worker is disabled. Set LicenseUsageSnapshot:Enabled=true to run it.");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                continue;
            }

            await TakeSnapshotAsync(stoppingToken);

            var intervalHours = await _settingsCache.GetIntAsync(
                "LicenseUsageSnapshot:IntervalHours", _options.Value.IntervalHours, stoppingToken);
            await Task.Delay(TimeSpan.FromHours(Math.Max(1, intervalHours)), stoppingToken);
        }
    }

    private async Task TakeSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var ledgerRepository = scope.ServiceProvider.GetRequiredService<IUsageLedgerRepository>();
            var usageCountsProvider = scope.ServiceProvider.GetRequiredService<ILicenseUsageCountsProvider>();
            var executionStatsProvider =
                scope.ServiceProvider.GetRequiredService<ILicenseUsageExecutionStatsProvider>();

            var tail = await ledgerRepository.GetTailAsync(cancellationToken);
            var nowUtc = DateTime.UtcNow;
            var monotonicTicks = Environment.TickCount64;

            if (tail is not null)
            {
                await CheckForClockRollbackAsync(scope.ServiceProvider, tail, nowUtc, monotonicTicks, cancellationToken);
            }

            var usageCounts = await usageCountsProvider.GetCurrentCountsAsync(cancellationToken);
            var executionStats = await executionStatsProvider.GetCurrentStatsAsync(cancellationToken);

            var entry = new UsageLedgerEntry(
                Guid.NewGuid(),
                nowUtc,
                monotonicTicks,
                usageCounts.UserCount,
                usageCounts.SourceConnectionCount,
                usageCounts.TenantCount,
                usageCounts.WorkflowCount,
                executionStats.CumulativeConfiguredPipelineRunCount,
                executionStats.CumulativeRuntimeWorkflowRunCount,
                executionStats.ProcessedRecordsThisMonth,
                tail?.EntryHash);

            // Anomalous or not, the row is always appended — never suppress a snapshot on the strength of a
            // rollback suspicion, since that would create exactly the gap in the record a tampering client
            // would want.
            await ledgerRepository.AppendAsync(entry, cancellationToken);

            _logger.LogInformation(
                "Appended usage ledger entry: Users={Users} SourceConnections={SourceConnections} " +
                "Tenants={Tenants} Workflows={Workflows} ProcessedRecordsThisMonth={ProcessedRecordsThisMonth}.",
                usageCounts.UserCount, usageCounts.SourceConnectionCount, usageCounts.TenantCount,
                usageCounts.WorkflowCount, executionStats.ProcessedRecordsThisMonth);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "License usage snapshot run failed.");
        }
    }

    /// <summary>Runs both anti-clock-rollback checks against the ledger's last row. Either one firing is
    /// logged Critical and raised as a <c>SecurityEvent</c> — but never blocks the snapshot from being
    /// appended (see <see cref="TakeSnapshotAsync"/>'s remarks).</summary>
    private async Task CheckForClockRollbackAsync(
        IServiceProvider scopedServices,
        UsageLedgerTail tail,
        DateTime nowUtc,
        long monotonicTicks,
        CancellationToken cancellationToken)
    {
        var crossRestartRollback = ClockRollbackDetector.IsCrossRestartRollbackSuspected(nowUtc, tail.ObservedUtc);
        var sameBootRollback = !crossRestartRollback && ClockRollbackDetector.IsSameBootRollbackSuspected(
            tail.ObservedUtc, tail.MonotonicTicks, nowUtc, monotonicTicks);

        if (!crossRestartRollback && !sameBootRollback)
        {
            return;
        }

        var reason = crossRestartRollback ? "cross-restart (wall-clock high-water-mark)" : "same-boot (monotonic-vs-wall-clock delta)";
        _logger.LogCritical(
            "Possible system clock rollback detected ({Reason} check) — current UTC {NowUtc} vs. last usage " +
            "ledger entry observed at {LastObservedUtc} (SequenceNumber {SequenceNumber}).",
            reason, nowUtc, tail.ObservedUtc, tail.SequenceNumber);

        var governanceLogger = scopedServices.GetRequiredService<IGovernanceLogger>();
        await governanceLogger.LogSecurityEventAsync(
            new SecurityEventEntry(
                "LicenseClockRollbackDetected",
                "Critical",
                Details: $"System clock ({nowUtc:O}) is inconsistent with the usage ledger's last recorded " +
                          $"observation ({tail.ObservedUtc:O}, SequenceNumber={tail.SequenceNumber}) — {reason} " +
                          "check triggered. Investigate possible tampering with the system clock to circumvent " +
                          "license usage tracking."),
            cancellationToken);
    }
}
