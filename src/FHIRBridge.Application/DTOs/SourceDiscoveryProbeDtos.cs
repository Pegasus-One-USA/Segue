namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Request to probe a FHIR endpoint by base URL (source-connection wizard, pre-create).
/// <paramref name="SourceConnectionId"/> is optional — set when the wizard's "Existing Connection" picker
/// selected an already-saved connection, so the backend can resolve that connection's own vendor and
/// authorize the probe against it (see SourceDiscoveryController.Probe); null for a genuinely new
/// connection, where no vendor can be resolved yet and the prior, generic-only authorization applies.
/// </summary>
public sealed record SourceDiscoveryProbeRequest(string BaseUrl, Guid? SourceConnectionId = null);

/// <summary>
/// Combined result of a pre-create endpoint probe: the SMART discovery document (OAuth endpoints + scopes) and the
/// resource types the endpoint supports. <see cref="ResourceTypesError"/> is set when <c>/metadata</c> could not be
/// read (e.g. the server requires auth for its conformance statement) so the wizard can still show the endpoints.
/// <see cref="SmartConfigurationError"/> is set when the server has no <c>/.well-known/smart-configuration</c> at
/// all — true of plain (non-SMART) FHIR R4 servers such as a bare HAPI instance — so the wizard can fall back to
/// manual endpoint entry instead of failing the whole probe.
/// </summary>
/// <remarks><see cref="SearchParametersByResourceType"/> carries, per resource type, the search parameter names the
/// CapabilityStatement declares (resource-level plus any common <c>rest.searchParam</c>), so the destination
/// wizard's Criteria editor can warn when a parameter will be ignored by the source. A type that declares none is
/// omitted — "nothing declared" is unknown, not "nothing supported".</remarks>
public sealed record SourceDiscoveryProbeResult(
    SmartConfigurationDto SmartConfiguration,
    IReadOnlyList<string> ResourceTypes,
    string? ResourceTypesError,
    string? SmartConfigurationError = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? SearchParametersByResourceType = null);

/// <summary>What a source's CapabilityStatement (<c>/metadata</c>) declares, read in one fetch: the resource types
/// with a read/search interaction (sorted) and each declared type's search parameter names.</summary>
public sealed record SourceEndpointCapabilities(
    IReadOnlyList<string> ResourceTypes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SearchParametersByResourceType);
