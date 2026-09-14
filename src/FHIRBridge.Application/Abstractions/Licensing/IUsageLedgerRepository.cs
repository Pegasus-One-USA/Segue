using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>
/// Lightweight summary of the ledger's last row — enough for
/// <c>LicenseUsageSnapshotWorker</c> to chain the next entry onto the right hash and run the
/// cross-restart clock-rollback check (<see cref="FHIRBridge.Domain.Entities.Licensing.ClockRollbackDetector.IsCrossRestartRollbackSuspected"/>)
/// without loading the whole table.
/// </summary>
public sealed record UsageLedgerTail(long SequenceNumber, string EntryHash, DateTime ObservedUtc, long MonotonicTicks);

/// <summary>
/// Persistence for the tamper-evident <see cref="UsageLedgerEntry"/> hash chain. Genuinely needs raw
/// DbContext-level access (no existing repository fits) — see <c>EfUsageLedgerRepository</c>. Writes here
/// are pure local DB operations with zero network dependency, independent of
/// <c>LicenseHeartbeatWorker</c>'s best-effort remote check-in.
/// </summary>
public interface IUsageLedgerRepository
{
    /// <summary>The most recently appended row's identity/hash/timestamps, or <c>null</c> when the ledger is
    /// empty (first-ever snapshot).</summary>
    Task<UsageLedgerTail?> GetTailAsync(CancellationToken cancellationToken);

    /// <summary>Appends one already-hash-computed entry. Never mutates or removes existing rows — the
    /// append-only guard (<see cref="FHIRBridge.SharedKernel.Abstractions.IAppendOnlyEntity"/>) blocks that
    /// centrally regardless.</summary>
    Task AppendAsync(UsageLedgerEntry entry, CancellationToken cancellationToken);

    /// <summary>Rows strictly after <paramref name="sequenceNumber"/>, oldest first, capped at
    /// <paramref name="maxCount"/> — available for a future admin/export view over the usage history.</summary>
    Task<IReadOnlyList<UsageLedgerEntry>> GetSinceSequenceAsync(
        long sequenceNumber, int maxCount, CancellationToken cancellationToken);
}
