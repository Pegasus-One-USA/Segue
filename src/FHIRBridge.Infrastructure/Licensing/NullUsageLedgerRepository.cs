using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>No-DB dev/test profile stand-in — mirrors <c>NullAuditChainVerificationService</c>/
/// <c>NullGovernanceLogger</c>'s "nothing to persist to, nothing to read back" reasoning for that same
/// profile. <c>LicenseUsageSnapshotWorker</c> still runs and calls this every tick; it simply has nowhere
/// durable to write, so every call is a no-op.</summary>
public sealed class NullUsageLedgerRepository : IUsageLedgerRepository
{
    public Task<UsageLedgerTail?> GetTailAsync(CancellationToken cancellationToken) =>
        Task.FromResult<UsageLedgerTail?>(null);

    public Task AppendAsync(UsageLedgerEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<UsageLedgerEntry>> GetSinceSequenceAsync(
        long sequenceNumber, int maxCount, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UsageLedgerEntry>>([]);
}
