namespace FHIRBridge.Application.DTOs;

/// <summary>
/// The SMART-on-FHIR discovery document a source publishes at <c>{baseUrl}/.well-known/smart-configuration</c>
/// (per the SMART App Launch spec). It advertises the OAuth endpoints and capabilities FHIRBridge needs to drive an
/// interactive authorization-code flow — complementing the resource-level <see cref="SourceCapabilityProfileDto"/>
/// derived from <c>/metadata</c>.
/// </summary>
public sealed record SmartConfigurationDto(
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? IntrospectionEndpoint,
    string? RevocationEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> ScopesSupported,
    IReadOnlyList<string> GrantTypesSupported,
    IReadOnlyList<string> ResponseTypesSupported,
    IReadOnlyList<string> CodeChallengeMethodsSupported,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> TokenEndpointAuthMethodsSupported)
{
    /// <summary>All-empty document for servers with no SMART discovery endpoint at all (plain FHIR R4 servers) —
    /// lets the wizard fall back to manual endpoint entry instead of failing the probe outright.</summary>
    public static SmartConfigurationDto Empty { get; } = new(
        null, null, null, null, null, [], [], [], [], [], []);
}
