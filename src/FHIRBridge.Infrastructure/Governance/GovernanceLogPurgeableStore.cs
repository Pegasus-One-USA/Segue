using System.Linq.Expressions;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Generic <see cref="IPurgeableStore"/> over any governance log entity with a UTC timestamp property — one
/// reusable class instead of nine near-identical hand-written ones. Archives expiring rows (via
/// <see cref="IGovernanceLogArchiveWriter"/>) before purging, then deletes via <c>ExecuteDeleteAsync</c> (a
/// set-based bulk delete translated directly to SQL, re-evaluating the same cutoff predicate) — so the delete
/// itself never touches EF change tracking or the <c>AuditingSaveChangesInterceptor</c> append-only guard,
/// which is exactly why this must never be registered for <c>AuditLog</c>, <c>AuthenticationLog</c>,
/// <c>SmartLaunchLog</c>, or <c>DataAccessLog</c>: those four are immutable, 7-year HIPAA audit evidence and
/// are deliberately never wired up as purgeable.
/// </summary>
public sealed class GovernanceLogPurgeableStore<TEntity> : IPurgeableStore
    where TEntity : class
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IGovernanceLogArchiveWriter _archiveWriter;
    private readonly Expression<Func<TEntity, DateTime>> _timestampSelector;

    public GovernanceLogPurgeableStore(
        FHIRBridgeDbContext dbContext,
        IGovernanceLogArchiveWriter archiveWriter,
        string dataClass,
        Expression<Func<TEntity, DateTime>> timestampSelector)
    {
        _dbContext = dbContext;
        _archiveWriter = archiveWriter;
        DataClass = dataClass;
        _timestampSelector = timestampSelector;
    }

    public string DataClass { get; }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var parameter = _timestampSelector.Parameters[0];
        var isOlderThanCutoff = Expression.Lambda<Func<TEntity, bool>>(
            Expression.LessThan(_timestampSelector.Body, Expression.Constant(cutoffUtc)),
            parameter);

        var query = _dbContext.Set<TEntity>().Where(isOlderThanCutoff);

        var expiringRows = await query.AsNoTracking().ToListAsync(cancellationToken);
        if (expiringRows.Count == 0)
        {
            return 0;
        }

        await _archiveWriter.ArchiveAsync(DataClass, expiringRows, cutoffUtc, cancellationToken);

        return await query.ExecuteDeleteAsync(cancellationToken);
    }
}
