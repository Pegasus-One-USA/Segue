namespace FHIRBridge.Infrastructure.Destinations.Auth;

/// <summary>
/// Acquires an access token for an outbound FHIR-repository destination (e.g. Aidbox) via OAuth2 client-credentials.
/// Deliberately separate from <c>IFhirAccessTokenProvider</c> (Runtime.Application) — that contract is keyed on
/// <c>FhirSourceConfiguration</c>, a source/EHR-shaped DTO, and its only implementation is a closed dispatcher over
/// known EHR vendors. A destination has no vendor axis and no source-only fields, so it gets its own small contract
/// instead of forcing a destination through a source-shaped abstraction.
/// </summary>
public interface IFhirDestinationTokenProvider
{
    Task<string> GetAccessTokenAsync(FhirDestinationOAuth2Options options, CancellationToken cancellationToken);
}

/// <summary>Client-credentials parameters for a destination-side OAuth2 token exchange.</summary>
public sealed record FhirDestinationOAuth2Options(
    string TokenEndpoint,
    string ClientId,
    string ClientSecret,
    string? Scope);
