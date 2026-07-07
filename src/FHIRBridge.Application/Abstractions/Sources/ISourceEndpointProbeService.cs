using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Probes a FHIR endpoint by its base URL alone — no persisted source connection required — so the source-connection
/// wizard can run SMART discovery and list supported resource types <em>before</em> the source is created. Both calls
/// hit public endpoints (<c>.well-known/smart-configuration</c> and <c>/metadata</c>) with no bearer token.
/// </summary>
public interface ISourceEndpointProbeService
{
    /// <summary>Fetches and parses <c>{baseUrl}/.well-known/smart-configuration</c>.</summary>
    Task<SmartConfigurationDto> ProbeSmartConfigurationAsync(string baseUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches <c>{baseUrl}/metadata</c> (CapabilityStatement) and returns the resource types the endpoint exposes
    /// with a read/search interaction, sorted. Anonymous — relies on the conformance statement being public.
    /// </summary>
    Task<IReadOnlyList<string>> ProbeSupportedResourceTypesAsync(string baseUrl, CancellationToken cancellationToken);
}
