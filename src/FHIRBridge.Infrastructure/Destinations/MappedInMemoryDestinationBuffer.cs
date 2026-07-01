using System.Collections.Concurrent;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Destinations;

public sealed class MappedInMemoryDestinationBuffer
{
    private readonly ConcurrentDictionary<Guid, List<MappedDestinationRecord>> _records = new();

    public void Add(Guid pipelineRunId, IEnumerable<MappedDestinationRecord> records)
    {
        var runRecords = _records.GetOrAdd(pipelineRunId, _ => []);

        lock (runRecords)
        {
            runRecords.AddRange(records);
        }
    }

    public IReadOnlyList<MappedDestinationRecord> Get(Guid pipelineRunId)
    {
        if (!_records.TryGetValue(pipelineRunId, out var records))
        {
            return [];
        }

        lock (records)
        {
            return records.ToList();
        }
    }
}
