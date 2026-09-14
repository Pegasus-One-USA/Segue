using System.Security.Cryptography;
using System.Text;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Licensing;

/// <summary>
/// Immutable, hash-chained snapshot of live product usage, taken periodically by
/// <c>LicenseUsageSnapshotWorker</c> — the local, offline-safe source of truth behind "how much of the
/// license is being used" and "how long has this install been active". Mirrors
/// <see cref="FHIRBridge.Domain.Entities.Governance.AuditLog"/>'s exact tamper-evidence shape: each row's
/// <see cref="EntryHash"/> is computed over its own fields plus the prior row's hash
/// (<see cref="PreviousHash"/>), so any retroactive edit or deletion of a row breaks the chain.
/// <see cref="SequenceNumber"/> (DB identity) gives a gap-free, monotonic ordering to chain against;
/// <see cref="Id"/> stays the opaque external key.
///
/// Two independent anti-clock-rollback signals ride along with every row (see
/// <see cref="FHIRBridge.Domain.Entities.Licensing.ClockRollbackDetector"/> for the detection heuristics
/// that use them):
///  - <see cref="ObservedUtc"/> lets a fresh <c>MAX(ObservedUtc)</c> scan of this table catch a
///    stop-the-service-and-wind-the-clock-back-then-restart rollback.
///  - <see cref="MonotonicTicks"/> (<see cref="Environment.TickCount64"/> at snapshot time) lets a
///    same-boot, no-restart clock change be detected by comparing the wall-clock delta between two
///    consecutive rows against the monotonic delta between them.
/// </summary>
public sealed class UsageLedgerEntry : Entity<Guid>, IAppendOnlyEntity
{
    private UsageLedgerEntry()
    {
    }

    public UsageLedgerEntry(
        Guid id,
        DateTime observedUtc,
        long monotonicTicks,
        int userCount,
        int sourceConnectionCount,
        int tenantCount,
        int workflowCount,
        long cumulativeConfiguredPipelineRunCount,
        long cumulativeRuntimeWorkflowRunCount,
        long processedRecordsThisMonth,
        string? previousHash)
    {
        Id = id;
        ObservedUtc = observedUtc;
        MonotonicTicks = monotonicTicks;
        UserCount = userCount;
        SourceConnectionCount = sourceConnectionCount;
        TenantCount = tenantCount;
        WorkflowCount = workflowCount;
        CumulativeConfiguredPipelineRunCount = cumulativeConfiguredPipelineRunCount;
        CumulativeRuntimeWorkflowRunCount = cumulativeRuntimeWorkflowRunCount;
        ProcessedRecordsThisMonth = processedRecordsThisMonth;
        PreviousHash = previousHash;
        EntryHash = ComputeHash(
            previousHash,
            observedUtc,
            monotonicTicks,
            userCount,
            sourceConnectionCount,
            tenantCount,
            workflowCount,
            cumulativeConfiguredPipelineRunCount,
            cumulativeRuntimeWorkflowRunCount,
            processedRecordsThisMonth);
    }

    /// <summary>DB-identity ordering column the hash chain links against — never exposed externally.</summary>
    public long SequenceNumber { get; private set; }

    public DateTime ObservedUtc { get; private set; }

    /// <summary><see cref="Environment.TickCount64"/> at the moment this row was created — a
    /// process-monotonic counter unaffected by wall-clock changes, used only to detect a same-boot clock
    /// rollback (see <see cref="FHIRBridge.Domain.Entities.Licensing.ClockRollbackDetector"/>). Resets to a
    /// small value on every process restart, so it is only ever compared against another row's value when
    /// both rows are known to share the same boot.</summary>
    public long MonotonicTicks { get; private set; }

    public int UserCount { get; private set; }
    public int SourceConnectionCount { get; private set; }
    public int TenantCount { get; private set; }
    public int WorkflowCount { get; private set; }

    /// <summary>All-time count of <c>ConfiguredPipelineRunRecord</c> rows — informational only, never gates
    /// anything.</summary>
    public long CumulativeConfiguredPipelineRunCount { get; private set; }

    /// <summary>All-time count of Runtime-plane <c>WorkflowRun</c> rows — informational only, never gates
    /// anything.</summary>
    public long CumulativeRuntimeWorkflowRunCount { get; private set; }

    /// <summary>Resources processed (fetched) within the current calendar month as of
    /// <see cref="ObservedUtc"/> — informational only, never gates anything.</summary>
    public long ProcessedRecordsThisMonth { get; private set; }

    public string? PreviousHash { get; private set; }
    public string EntryHash { get; private set; } = default!;

    /// <summary>
    /// The hash-chain function — recomputable independently of any live instance, same reasoning as
    /// <see cref="FHIRBridge.Domain.Entities.Governance.AuditLog.ComputeHash"/>.
    /// </summary>
    public static string ComputeHash(
        string? previousHash,
        DateTime observedUtc,
        long monotonicTicks,
        int userCount,
        int sourceConnectionCount,
        int tenantCount,
        int workflowCount,
        long cumulativeConfiguredPipelineRunCount,
        long cumulativeRuntimeWorkflowRunCount,
        long processedRecordsThisMonth)
    {
        var payload = string.Join(
            '|',
            previousHash,
            observedUtc.Ticks,
            monotonicTicks,
            userCount,
            sourceConnectionCount,
            tenantCount,
            workflowCount,
            cumulativeConfiguredPipelineRunCount,
            cumulativeRuntimeWorkflowRunCount,
            processedRecordsThisMonth);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>True if this row's stored <see cref="EntryHash"/> matches what its own fields recompute to.</summary>
    public bool VerifyOwnHash() =>
        EntryHash == ComputeHash(
            PreviousHash,
            ObservedUtc,
            MonotonicTicks,
            UserCount,
            SourceConnectionCount,
            TenantCount,
            WorkflowCount,
            CumulativeConfiguredPipelineRunCount,
            CumulativeRuntimeWorkflowRunCount,
            ProcessedRecordsThisMonth);
}
