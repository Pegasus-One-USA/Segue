using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

public interface IFhirSourceClient : ISourceConnector
{
    /// <summary>FHIR sources speak FHIR R4 REST; vendor subclasses inherit this without restating it.</summary>
    SourceConnectorKind ISourceConnector.Kind => SourceConnectorKind.FhirRest;

    Task<IReadOnlyList<ResourceEnvelope>> SearchAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
