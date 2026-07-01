using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

public sealed class MappedInMemoryDestinationWriter : IConfiguredDestinationWriter
{
    private readonly MappedInMemoryDestinationBuffer _buffer;

    public MappedInMemoryDestinationWriter(MappedInMemoryDestinationBuffer buffer)
    {
        _buffer = buffer;
    }

    public Task<int> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var group in records.GroupBy(x => x.PipelineRunId))
        {
            _buffer.Add(group.Key, group);
        }

        return Task.FromResult(records.Count);
    }
}
