using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// EF store for the EHR write-back ledger. The context is the run's shared scoped one (the orchestrator, executors
/// and their dependencies all use it), so a failed insert is detached at once rather than left tracked to poison the
/// next save.
/// </summary>
public sealed class EfEhrWriteLedgerRepository : IEhrWriteLedgerRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfEhrWriteLedgerRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<string, EhrWriteLedgerEntry>> FindAsync(
        string targetKey,
        string resourceType,
        IReadOnlyCollection<string> sourceKeys,
        CancellationToken cancellationToken)
    {
        if (sourceKeys.Count == 0)
        {
            return new Dictionary<string, EhrWriteLedgerEntry>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, EhrWriteLedgerEntry>(StringComparer.Ordinal);

        // Chunked so a large batch never builds an IN list past either provider's parameter limit.
        foreach (var chunk in sourceKeys.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var rows = await _db.EhrWriteLedgerEntries
                .Where(x => x.TargetKey == targetKey && x.ResourceType == resourceType && chunk.Contains(x.SourceKey))
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                result[row.SourceKey] = row;
            }
        }

        return result;
    }

    public async Task<bool> TryAddAsync(EhrWriteLedgerEntry entry, CancellationToken cancellationToken)
    {
        await _db.EhrWriteLedgerEntries.AddAsync(entry, cancellationToken);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsIdempotencyViolation(exception))
        {
            _db.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> TryClaimAsync(EhrWriteLedgerEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // AttemptCount moved under us: another run claimed the row. Drop our stale copy.
            _db.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    private static bool IsIdempotencyViolation(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException postgres =>
                postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && postgres.ConstraintName == EhrWriteLedgerEntryConfiguration.IdempotencyIndexName,
            SqlException sql =>
                sql.Number is 2601 or 2627
                && sql.Message.Contains(EhrWriteLedgerEntryConfiguration.IdempotencyIndexName, StringComparison.Ordinal),
            _ => false,
        };
}
