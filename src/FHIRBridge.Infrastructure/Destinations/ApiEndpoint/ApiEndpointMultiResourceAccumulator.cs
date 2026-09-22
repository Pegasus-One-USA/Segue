using System.Collections.Concurrent;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <inheritdoc cref="IApiEndpointMultiResourceAccumulator"/>
public sealed class ApiEndpointMultiResourceAccumulator : IApiEndpointMultiResourceAccumulator
{
    private sealed record Key(Guid DestinationId, Guid PipelineRunId);

    // Outer dictionary keyed by (destination, run); inner by resource type. A plain `lock` around the whole
    // read-check-clear sequence in TryTakeComplete is simpler to reason about than a lock-free completeness
    // check here — multi-resource writes are, by definition, low-frequency (one destination write per resource
    // type per run, not a hot per-record path), so contention is a non-issue.
    private readonly ConcurrentDictionary<Key, Dictionary<string, (MappingProfile MappingProfile, IReadOnlyCollection<MappedDestinationRecord> Records)>> _pending = new();
    private readonly object _sync = new();

    public void Add(
        Guid destinationId,
        Guid pipelineRunId,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var key = new Key(destinationId, pipelineRunId);
        lock (_sync)
        {
            var byResourceType = _pending.GetOrAdd(key, static _ => new(StringComparer.OrdinalIgnoreCase));
            byResourceType[mappingProfile.ResourceType] = (mappingProfile, records);
        }
    }

    public bool TryTakeComplete(
        Guid destinationId,
        Guid pipelineRunId,
        IReadOnlyCollection<string> expectedResourceTypes,
        out IReadOnlyList<(MappingProfile MappingProfile, IReadOnlyCollection<MappedDestinationRecord> Records)> batches)
    {
        var key = new Key(destinationId, pipelineRunId);
        lock (_sync)
        {
            if (_pending.TryGetValue(key, out var byResourceType)
                && expectedResourceTypes.All(rt => byResourceType.ContainsKey(rt)))
            {
                batches = byResourceType.Values.ToList();
                _pending.TryRemove(key, out _);
                return true;
            }
        }

        batches = [];
        return false;
    }
}
