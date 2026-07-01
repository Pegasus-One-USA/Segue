using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// In-memory, thread-safe lineage store and query service. Mirrors the existing in-memory store pattern used
/// elsewhere; a durable EF-backed implementation can replace it without changing the abstraction. Implements
/// <see cref="IPurgeableStore"/> so expired lineage can be removed by the retention purge job.
/// </summary>
public sealed class InMemoryLineageStore : ILineageStore, ILineageQueryService, IPurgeableStore
{
    private readonly List<ResourceLineageRecord> _records = [];
    private readonly Lock _gate = new();

    public string DataClass => "ResourceLineage";

    public Task AppendAsync(ResourceLineageRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ResourceLineageRecord>> QueryAsync(LineageQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<ResourceLineageRecord> matches = _records
                .Where(r => r.TenantId == query.TenantId)
                .Where(r => query.PipelineRunId is null || r.PipelineRunId == query.PipelineRunId)
                .Where(r => query.ResourceType is null || string.Equals(r.ResourceType, query.ResourceType, StringComparison.OrdinalIgnoreCase))
                .Where(r => query.SourceResourceId is null || string.Equals(r.SourceResourceId, query.SourceResourceId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.OccurredOnUtc)
                .ToList();

            return Task.FromResult(matches);
        }
    }

    public async Task<ResourceLineageChain> GetChainAsync(LineageQuery query, CancellationToken cancellationToken)
    {
        var steps = await QueryAsync(query, cancellationToken);
        return new ResourceLineageChain(query.TenantId, query.SourceResourceId, steps);
    }

    public Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var removed = _records.RemoveAll(r => r.OccurredOnUtc < cutoffUtc);
            return Task.FromResult(removed);
        }
    }
}
