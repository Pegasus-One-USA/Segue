using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Discovers a source FHIR endpoint's CapabilityStatement (<c>/metadata</c>), persists it as a
/// <see cref="SourceCapabilityProfileDto"/> snapshot, and exposes the latest snapshot for reads.
/// </summary>
public interface ISourceCapabilityDiscoveryService
{
    /// <summary>Fetches <c>/metadata</c>, persists a fresh snapshot, and returns it.</summary>
    Task<SourceCapabilityProfileDto> DiscoverAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken);

    /// <summary>Returns the latest persisted snapshot, or null if discovery has never run for this source.</summary>
    Task<SourceCapabilityProfileDto?> GetAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken);
}
