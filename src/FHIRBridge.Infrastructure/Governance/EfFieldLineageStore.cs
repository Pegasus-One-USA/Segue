using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Durable, EF-backed field-level lineage store. Persists to <c>FieldLineageEntries</c> so it survives restarts and
/// is queryable across instances. Implements <see cref="IPurgeableStore"/> so it's removed by the retention purge
/// job on the same schedule as resource-level lineage — the primary volume control is the opt-in gate at the
/// source (<c>FieldLineageOptions</c>), not a dedicated shorter retention window.
/// </summary>
public sealed class EfFieldLineageStore : IFieldLineageStore, IFieldLineageQueryService, IPurgeableStore
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfFieldLineageStore(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public string DataClass => "FieldLineage";

    public async Task AppendAsync(FieldLineageRecord record, CancellationToken cancellationToken)
    {
        await _dbContext.FieldLineageEntries.AddAsync(
            new FieldLineageEntry(
                record.PipelineRunId,
                record.MappingProfileId,
                record.ResourceType,
                record.SourceResourceId,
                record.SourceFieldPath,
                record.TransformationType,
                record.DestinationObject,
                record.DestinationColumn,
                record.OccurredOnUtc),
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FieldLineageRecord>> GetFieldsAsync(
        FieldLineageQuery query,
        CancellationToken cancellationToken)
    {
        var entries = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.ResourceType == query.ResourceType && x.SourceResourceId == query.SourceResourceId)
            .Where(x => query.PipelineRunId == null || x.PipelineRunId == query.PipelineRunId)
            .OrderBy(x => x.OccurredOnUtc)
            .ToListAsync(cancellationToken);

        return entries
            .Select(x => new FieldLineageRecord(
                x.PipelineRunId,
                x.MappingProfileId,
                x.ResourceType,
                x.SourceResourceId,
                x.SourceFieldPath,
                x.TransformationType,
                x.DestinationObject,
                x.DestinationColumn,
                x.OccurredOnUtc))
            .ToList();
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var expired = await _dbContext.FieldLineageEntries
            .Where(x => x.OccurredOnUtc < cutoffUtc)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        _dbContext.FieldLineageEntries.RemoveRange(expired);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }
}
