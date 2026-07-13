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
        Guid sourceConnectionId,
        CancellationToken cancellationToken);

    /// <summary>Returns the latest persisted snapshot, or null if discovery has never run for this source.</summary>
    Task<SourceCapabilityProfileDto?> GetAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the source's public SMART discovery document (<c>{baseUrl}/.well-known/smart-configuration</c>) and
    /// returns its advertised OAuth endpoints + capabilities. Unlike <see cref="DiscoverAsync"/> this is not
    /// persisted — it is read on demand to configure an interactive authorization-code connection.
    /// <paramref name="overrideBaseUrl"/>, when set, is fetched from instead of the source connection's own
    /// configured base URL — used when a launch targets a specific hospital/organization endpoint (from the
    /// <c>EhrEndpoints</c> directory) rather than the connection's default.
    /// </summary>
    Task<SmartConfigurationDto> DiscoverSmartConfigurationAsync(
        Guid sourceConnectionId,
        string? overrideBaseUrl,
        CancellationToken cancellationToken);
}
