using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;

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
        return new ResourceLineageChain(query.SourceResourceId, steps);
    }

    public Task<PagedResult<ResourceLineageRecord>> GetPagedAsync(
        LineageListFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var query = _records
                .Where(r => filter.PipelineRunId is null || r.PipelineRunId == filter.PipelineRunId)
                .Where(r => filter.ResourceType is null || string.Equals(r.ResourceType, filter.ResourceType, StringComparison.OrdinalIgnoreCase))
                .Where(r => filter.Action is null || string.Equals(r.Action, filter.Action, StringComparison.OrdinalIgnoreCase))
                .Where(r => filter.Status is null || string.Equals(r.Status, filter.Status, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.OccurredOnUtc)
                .ToList();

            var take = Math.Clamp(pageSize, 1, 200);
            var skip = Math.Max(0, (page - 1) * take);

            return Task.FromResult(new PagedResult<ResourceLineageRecord>(
                query.Skip(skip).Take(take).ToList(),
                query.Count,
                page,
                take));
        }
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
