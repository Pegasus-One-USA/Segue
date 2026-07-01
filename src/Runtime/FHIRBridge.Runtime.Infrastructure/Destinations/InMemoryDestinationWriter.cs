using FHIRBridge.Runtime.Application.Abstractions.Destinations;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Entities;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Infrastructure.Destinations;

public sealed class InMemoryDestinationWriter : IDestinationWriter
{
    private readonly InMemoryDestinationBuffer _buffer;

    public InMemoryDestinationWriter(InMemoryDestinationBuffer buffer)
    {
        _buffer = buffer;
    }

    public Task<int> WriteAsync(
        PipelineRun pipelineRun,
        RuntimeDestinationConfiguration destination,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        _buffer.Add(pipelineRun.Id, resources);

        return Task.FromResult(resources.Count);
    }
}
