using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Store for <see cref="EhrWriteLedgerEntry"/> rows, the idempotency guard of EHR write-back. A row is unique per
/// (target environment, resource type, source record); see the entity for why it is not keyed on ids.
/// </summary>
public interface IEhrWriteLedgerRepository
{
    /// <summary>Rows for the given source records in one target environment and resource type, keyed by
    /// <see cref="EhrWriteLedgerEntry.SourceKey"/>.</summary>
    Task<IReadOnlyDictionary<string, EhrWriteLedgerEntry>> FindAsync(
        string targetKey,
        string resourceType,
        IReadOnlyCollection<string> sourceKeys,
        CancellationToken cancellationToken);

    /// <summary>Inserts a new row. Returns false, without throwing, when a row with the same key already exists
    /// (another run claimed the same record first); the caller must then not send.</summary>
    Task<bool> TryAddAsync(EhrWriteLedgerEntry entry, CancellationToken cancellationToken);

    /// <summary>Saves a claim (<see cref="EhrWriteLedgerEntry.MarkSending"/>) on an existing row, only if the row has
    /// not changed since it was read. Returns false when another run claimed it first; the caller must then not
    /// send.</summary>
    Task<bool> TryClaimAsync(EhrWriteLedgerEntry entry, CancellationToken cancellationToken);

    /// <summary>Persists state changes made to rows returned by this repository.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Rows a person must resolve (<see cref="EhrWriteLedgerState.NeedsReview"/>), newest first, optionally
    /// narrowed to one resource type.</summary>
    Task<(IReadOnlyList<EhrWriteLedgerEntry> Items, int TotalCount)> ListNeedingReviewAsync(
        string? resourceType,
        DateTime utcNow,
        int skip,
        int take,
        CancellationToken cancellationToken);

    Task<EhrWriteLedgerEntry?> GetAsync(Guid id, CancellationToken cancellationToken);
}
