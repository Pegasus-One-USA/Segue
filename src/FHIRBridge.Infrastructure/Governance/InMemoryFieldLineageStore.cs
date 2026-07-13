using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>In-memory, thread-safe field-level lineage store. Mirrors <see cref="InMemoryLineageStore"/>; a durable
/// EF-backed implementation (<see cref="EfFieldLineageStore"/>) replaces it whenever a database connection is
/// configured.</summary>
public sealed class InMemoryFieldLineageStore : IFieldLineageStore, IFieldLineageQueryService, IPurgeableStore
{
    private readonly List<FieldLineageRecord> _records = [];
    private readonly Lock _gate = new();

    public string DataClass => "FieldLineage";

    public Task AppendAsync(FieldLineageRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FieldLineageRecord>> GetFieldsAsync(FieldLineageQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<FieldLineageRecord> matches = _records
                .Where(r => string.Equals(r.ResourceType, query.ResourceType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.SourceResourceId, query.SourceResourceId, StringComparison.OrdinalIgnoreCase))
                .Where(r => query.PipelineRunId is null || r.PipelineRunId == query.PipelineRunId)
                .OrderBy(r => r.OccurredOnUtc)
                .ToList();

            return Task.FromResult(matches);
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
