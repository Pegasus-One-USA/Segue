using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

public interface IFhirSourceClient
{
    Task<IReadOnlyList<ResourceEnvelope>> SearchAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
