using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Domain.Entities.Licensing;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>DB-backed <see cref="IUsageLedgerRepository"/> — a pure local read/write against
/// <c>UsageLedgerEntries</c>, zero network dependency. Mirrors the tail-hash query pattern already used by
/// <see cref="FHIRBridge.Infrastructure.Persistence.AuditingSaveChangesInterceptor"/> for <c>AuditLog</c>.</summary>
public sealed class EfUsageLedgerRepository : IUsageLedgerRepository
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfUsageLedgerRepository(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<UsageLedgerTail?> GetTailAsync(CancellationToken cancellationToken)
    {
        var tail = await _dbContext.UsageLedgerEntries
            .AsNoTracking()
            .OrderByDescending(x => x.SequenceNumber)
            .Select(x => new { x.SequenceNumber, x.EntryHash, x.ObservedUtc, x.MonotonicTicks })
            .FirstOrDefaultAsync(cancellationToken);

        return tail is null
            ? null
            : new UsageLedgerTail(tail.SequenceNumber, tail.EntryHash, tail.ObservedUtc, tail.MonotonicTicks);
    }

    public async Task AppendAsync(UsageLedgerEntry entry, CancellationToken cancellationToken)
    {
        _dbContext.UsageLedgerEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UsageLedgerEntry>> GetSinceSequenceAsync(
        long sequenceNumber, int maxCount, CancellationToken cancellationToken)
    {
        return await _dbContext.UsageLedgerEntries
            .AsNoTracking()
            .Where(x => x.SequenceNumber > sequenceNumber)
            .OrderBy(x => x.SequenceNumber)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
    }
}
