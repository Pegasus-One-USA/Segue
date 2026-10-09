using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Ledger for the no-database host profile (integration tests, local runs without a database). Rows are
/// held by reference, so state changes made by the caller are visible without a save.</summary>
public sealed class InMemoryEhrWriteLedgerRepository : IEhrWriteLedgerRepository
{
    private readonly ConcurrentDictionary<(string TargetKey, string ResourceType, string SourceKey), EhrWriteLedgerEntry> _rows = new();

    public Task<IReadOnlyDictionary<string, EhrWriteLedgerEntry>> FindAsync(
        string targetKey,
        string resourceType,
        IReadOnlyCollection<string> sourceKeys,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, EhrWriteLedgerEntry>(StringComparer.Ordinal);
        foreach (var sourceKey in sourceKeys)
        {
            if (_rows.TryGetValue((targetKey, resourceType, sourceKey), out var row))
            {
                result[sourceKey] = row;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, EhrWriteLedgerEntry>>(result);
    }

    public Task<bool> TryAddAsync(EhrWriteLedgerEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryAdd((entry.TargetKey, entry.ResourceType, entry.SourceKey), entry));

    /// <summary>Rows are shared by reference in this profile, so there is no stale copy to detect.</summary>
    public Task<bool> TryClaimAsync(EhrWriteLedgerEntry entry, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<(IReadOnlyList<EhrWriteLedgerEntry> Items, int TotalCount)> ListNeedingReviewAsync(
        string? resourceType,
        DateTime utcNow,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var matching = _rows.Values
            .Where(x => EhrWriteLedgerState.NeedsReview(x.State, x.UpdatedOnUtc, utcNow))
            .Where(x => string.IsNullOrWhiteSpace(resourceType) || x.ResourceType == resourceType)
            .OrderByDescending(x => x.UpdatedOnUtc)
            .ThenBy(x => x.Id)
            .ToList();
        IReadOnlyList<EhrWriteLedgerEntry> page = matching.Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, 200)).ToList();
        return Task.FromResult((page, matching.Count));
    }

    public Task<EhrWriteLedgerEntry?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.Values.FirstOrDefault(x => x.Id == id));
}
