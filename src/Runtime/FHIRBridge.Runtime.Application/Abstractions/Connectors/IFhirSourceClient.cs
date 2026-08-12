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

    /// <summary>Direct FHIR "read" (<c>GET {baseUrl}/{resourceType}/{id}</c>) — distinct from <see cref="SearchAsync"/>'s
    /// search interaction. Needed because some resource types don't reliably resolve via a <c>?_id=</c> search on
    /// every server (confirmed on Epic: <c>Group?_id=X</c> can reject an id that <c>GET Group/X</c> resolves fine),
    /// so a caller that already knows the exact id (e.g. resolving a bulk-export Group's membership) should read it
    /// directly rather than search for it. Returns null when the resource doesn't exist (404).</summary>
    Task<ResourceEnvelope?> ReadByIdAsync(
        string resourceType,
        string id,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
