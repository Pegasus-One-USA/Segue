using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Entities;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Application.Abstractions.Destinations;

public interface IDestinationWriter
{
    Task<int> WriteAsync(
        PipelineRun pipelineRun,
        RuntimeDestinationConfiguration destination,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken);
}
