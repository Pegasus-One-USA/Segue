using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Anonymous-safe listing/validation for EndpointType.MyChart rows (a specific customer/hospital's own branded
/// production instance) — the Patient Standalone counterpart of <see cref="IEhrEndpointService"/>'s Epic-sandbox-only
/// surface. Kept as its own service/interface (rather than adding methods to IEhrEndpointService) so the Provider
/// Standalone flow's existing Epic-only surface never needs to change to support this.
/// </summary>
public interface IPatientStandaloneEhrEndpointService
{
    /// <summary>Anonymous-safe listing/search of MyChart-typed endpoints only — backs the public
    /// ehr-mychart-endpoints controller. <paramref name="search"/> is an optional case-insensitive contains-match on
    /// Name.</summary>
    Task<IReadOnlyList<PublicEhrEpicEndpointDto>> GetPublicMyChartEndpointsAsync(
        string? search, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="ehrEndpointId"/> resolves to an EndpointType.MyChart row — validates a
    /// request-time id came from the same restricted set <see cref="GetPublicMyChartEndpointsAsync"/> exposes, not an
    /// arbitrary/other-typed (e.g. the shared Epic sandbox) row.</summary>
    Task<bool> IsMyChartEndpointAsync(Guid ehrEndpointId, CancellationToken cancellationToken);
}
