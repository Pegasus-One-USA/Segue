using FHIRBridge.Integration.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

public sealed class SampleFhirSourceClient : IFhirSourceClient
{
    public Task<IReadOnlyList<ResourceEnvelope>> SearchAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var json = SampleResources.GetResourceJson(resourceType);
        var envelope = FhirResourceParser.ParseResource(json);

        return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([envelope]);
    }
}
