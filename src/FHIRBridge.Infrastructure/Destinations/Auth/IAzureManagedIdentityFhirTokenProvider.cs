namespace FHIRBridge.Infrastructure.Destinations.Auth;

/// <summary>
/// Acquires an access token for an outbound FHIR destination (e.g. Azure Health Data Services' FHIR API) via
/// Azure AD managed identity, as an alternative to <see cref="IFhirDestinationTokenProvider"/>'s client-credentials
/// (client secret) flow — no Key Vault secret is ever resolved for this path. Separate interface, mirroring the
/// split between <c>IFhirDestinationTokenProvider</c> (client-credentials) and blob storage's own auth-mode
/// dispatch (<c>BlobContainerClientFactory</c>), rather than overloading one provider with both shapes.
/// </summary>
public interface IAzureManagedIdentityFhirTokenProvider
{
    Task<string> GetAccessTokenAsync(
        string resourceScope,
        string? managedIdentityClientId,
        string? authorityHost,
        CancellationToken cancellationToken);
}
