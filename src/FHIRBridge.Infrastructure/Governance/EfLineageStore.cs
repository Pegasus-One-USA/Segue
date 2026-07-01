using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Durable, EF-backed lineage store and query service. Persists the chain of custody to the
/// <c>ResourceLineageEntries</c> table so lineage survives process restarts and is queryable across instances.
/// Implements <see cref="IPurgeableStore"/> so expired lineage is removed by the retention purge job.
/// Replaces <see cref="InMemoryLineageStore"/> whenever a database connection is configured.
/// </summary>
public sealed class EfLineageStore : ILineageStore, ILineageQueryService, IPurgeableStore
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfLineageStore(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public string DataClass => "ResourceLineage";

    public async Task AppendAsync(ResourceLineageRecord record, CancellationToken cancellationToken)
    {
        await _dbContext.ResourceLineageEntries.AddAsync(
            new ResourceLineageEntry(
                record.TenantId,
                record.PipelineRunId,
                record.RouteId,
                record.SourceConnectionId,
                record.DestinationId,
                record.MappingProfileId,
                record.ResourceType,
                record.SourceResourceId,
                record.Action,
                record.Status,
                record.OccurredOnUtc),
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ResourceLineageRecord>> QueryAsync(
        LineageQuery query,
        CancellationToken cancellationToken)
    {
        var entries = await _dbContext.ResourceLineageEntries
            .AsNoTracking()
            .Where(x => x.TenantId == query.TenantId)
            .Where(x => query.PipelineRunId == null || x.PipelineRunId == query.PipelineRunId)
            .Where(x => query.ResourceType == null || x.ResourceType == query.ResourceType)
            .Where(x => query.SourceResourceId == null || x.SourceResourceId == query.SourceResourceId)
            .OrderBy(x => x.OccurredOnUtc)
            .ToListAsync(cancellationToken);

        IReadOnlyList<ResourceLineageRecord> records = entries
            .Select(x => new ResourceLineageRecord(
                x.TenantId,
                x.PipelineRunId,
                x.RouteId,
                x.SourceConnectionId,
                x.DestinationId,
                x.MappingProfileId,
                x.ResourceType,
                x.SourceResourceId,
                x.Action,
                x.Status,
                x.OccurredOnUtc))
            .ToList();

        return records;
    }

    public async Task<ResourceLineageChain> GetChainAsync(LineageQuery query, CancellationToken cancellationToken)
    {
        var steps = await QueryAsync(query, cancellationToken);
        return new ResourceLineageChain(query.TenantId, query.SourceResourceId, steps);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var expired = await _dbContext.ResourceLineageEntries
            .Where(x => x.OccurredOnUtc < cutoffUtc)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        _dbContext.ResourceLineageEntries.RemoveRange(expired);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }
}
