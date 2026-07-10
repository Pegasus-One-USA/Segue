namespace FHIRBridge.Application.DTOs;

/// <summary>Request to probe a FHIR endpoint by base URL (source-connection wizard, pre-create).</summary>
public sealed record SourceDiscoveryProbeRequest(string BaseUrl);

/// <summary>
/// Combined result of a pre-create endpoint probe: the SMART discovery document (OAuth endpoints + scopes) and the
/// resource types the endpoint supports. <see cref="ResourceTypesError"/> is set when <c>/metadata</c> could not be
/// read (e.g. the server requires auth for its conformance statement) so the wizard can still show the endpoints.
/// <see cref="SmartConfigurationError"/> is set when the server has no <c>/.well-known/smart-configuration</c> at
/// all — true of plain (non-SMART) FHIR R4 servers such as a bare HAPI instance — so the wizard can fall back to
/// manual endpoint entry instead of failing the whole probe.
/// </summary>
public sealed record SourceDiscoveryProbeResult(
    SmartConfigurationDto SmartConfiguration,
    IReadOnlyList<string> ResourceTypes,
    string? ResourceTypesError,
    string? SmartConfigurationError = null);
