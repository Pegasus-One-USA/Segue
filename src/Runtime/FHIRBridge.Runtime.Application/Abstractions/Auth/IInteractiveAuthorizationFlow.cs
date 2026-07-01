using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

/// <summary>
/// The interactive SMART-on-FHIR authorization-code (+ PKCE) round-trip, split into the two halves an OAuth callback
/// needs: <see cref="BuildAuthorizationRequest"/> starts the sign-in (returning the redirect URL plus the PKCE
/// verifier/state to retain), and <see cref="ExchangeAuthorizationCodeAsync"/> completes it from the callback,
/// persisting the token. Abstracted here so the callback orchestration can drive it without depending on the
/// concrete provider in the infrastructure layer.
/// </summary>
public interface IInteractiveAuthorizationFlow
{
    /// <summary>
    /// Builds the authorization-endpoint redirect and the PKCE verifier/state to retain for the callback. Pass
    /// <paramref name="launch"/> for an EHR launch (the opaque launch token) to include the launch scope and token.
    /// </summary>
    SmartAuthorizationRequest BuildAuthorizationRequest(
        FhirSourceConfiguration source,
        string redirectUri,
        string state,
        string? launch = null);

    /// <summary>Exchanges the authorization code (with the retained PKCE verifier) for a token and persists it.</summary>
    Task<string> ExchangeAuthorizationCodeAsync(
        FhirSourceConfiguration source,
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken);
}
