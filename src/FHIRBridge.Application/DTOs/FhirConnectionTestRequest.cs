namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc FHIR repository connection details for a not-yet-saved <c>FhirRepository</c> destination, used by the
/// destination wizard's Test Connection button. Mirrors <see cref="SftpConnectionTestRequest"/>'s "no secret
/// persisted" shape — credentials round-trip through the browser only for the duration of this one test call.
/// Only the fields relevant to <see cref="AuthType"/> need to be populated; the rest are ignored. No
/// <c>TokenEndpoint</c> field — for "clientcredentials"/"oauth2" it's discovered from <c>BaseUrl</c> via
/// <c>GET {BaseUrl}/.well-known/smart-configuration</c> rather than typed by the user (see
/// FhirDestinationConnectionTestService.DiscoverTokenEndpointAsync).
/// </summary>
public sealed record FhirConnectionTestRequest(
    string BaseUrl,
    string AuthType,
    string? ClientId,
    string? ClientSecret,
    string? Username,
    string? Password,
    string? BearerToken);
