using System.Collections.Concurrent;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Infrastructure.Destinations;

public sealed class InMemoryDestinationBuffer
{
    private readonly ConcurrentDictionary<Guid, List<ResourceEnvelope>> _resources = new();

    public void Add(Guid pipelineRunId, IEnumerable<ResourceEnvelope> resources)
    {
        var runResources = _resources.GetOrAdd(pipelineRunId, _ => []);

        lock (runResources)
        {
            runResources.AddRange(resources);
        }
    }

    public IReadOnlyList<ResourceEnvelope> Get(Guid pipelineRunId)
    {
        if (!_resources.TryGetValue(pipelineRunId, out var resources))
        {
            return [];
        }

        lock (resources)
        {
            return resources.ToList();
        }
    }
}
