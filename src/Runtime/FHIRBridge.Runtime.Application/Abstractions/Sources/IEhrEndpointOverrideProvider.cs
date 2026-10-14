namespace FHIRBridge.Runtime.Application.Abstractions.Sources;

/// <summary>The hospital-specific connection values of one EHR Endpoint, used to run a workflow against that
/// hospital instead of the source connection's saved configuration. Every value except <see cref="Id"/>,
/// <see cref="Name"/>, <see cref="Vendor"/> and <see cref="BaseUrl"/> is optional; a blank one keeps the source
/// connection's own value.</summary>
public sealed record EhrEndpointOverride(
    Guid Id,
    string Name,
    string Vendor,
    string BaseUrl,
    string? TokenEndpoint,
    string? ClientId,
    string? KeyId,
    string? JwksUrl,
    string? PracticeId);

/// <summary>Looks up an EHR Endpoint by the code a caller passed. Only consulted when a run explicitly names an EHR
/// Endpoint — runs without one never touch this.</summary>
public interface IEhrEndpointOverrideProvider
{
    /// <summary>Returns null when the code is not a known EHR Endpoint id.</summary>
    Task<EhrEndpointOverride?> ResolveAsync(string ehrEndpointCode, CancellationToken cancellationToken);
}
