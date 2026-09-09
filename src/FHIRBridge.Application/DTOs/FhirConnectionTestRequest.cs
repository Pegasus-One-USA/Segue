namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc FHIR repository connection details for a not-yet-saved <c>FhirRepository</c>/<c>AzureFhirService</c>
/// destination, used by the destination wizard's Test Connection button. Mirrors <see cref="SftpConnectionTestRequest"/>'s
/// "no secret persisted" shape — credentials round-trip through the browser only for the duration of this one
/// test call. Only the fields relevant to <see cref="AuthType"/> need to be populated; the rest are ignored.
/// For "clientcredentials"/"oauth2" with no <see cref="TenantId"/>, the token endpoint is discovered from
/// <c>BaseUrl</c> via <c>GET {BaseUrl}/.well-known/smart-configuration</c> (Aidbox/SMART-conformant servers) —
/// see FhirDestinationConnectionTestService.DiscoverTokenEndpointAsync. When <see cref="TenantId"/> IS supplied
/// (Azure FHIR Service), the token endpoint is instead computed directly as
/// <c>https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token</c>, skipping discovery entirely — Azure's
/// FHIR service doesn't need it since the tenant is already known.
/// </summary>
public sealed record FhirConnectionTestRequest(
    string BaseUrl,
    string AuthType,
    string? ClientId,
    string? ClientSecret,
    string? Username,
    string? Password,
    string? BearerToken,
    string? TenantId = null,
    string? Scope = null,
    string? ManagedIdentityClientId = null,
    string? AuthorityHost = null,
    // When re-testing an already-saved destination without retyping its secret (ClientSecret/Password/
    // BearerToken left blank), carries the destination's id so the test service can resolve the stored one via
    // ISecretProvider instead — mirrors MongoConnectionTestRequest.DestinationId/BlobConnectionTestRequest.
    // DestinationId.
    Guid? DestinationId = null);
