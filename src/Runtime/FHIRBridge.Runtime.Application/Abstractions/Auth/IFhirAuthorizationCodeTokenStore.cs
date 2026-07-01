using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

/// <summary>
/// Persists the tokens minted by an interactive OAuth 2.0 authorization-code flow (e.g. Healow), keyed per source.
/// The OAuth callback writes the freshly exchanged token; the access-token provider reads it back on each pipeline
/// run and refreshes it in place when it nears expiry.
/// </summary>
public interface IFhirAuthorizationCodeTokenStore
{
    Task<StoredOAuthToken?> GetAsync(string key, CancellationToken cancellationToken);

    Task SaveAsync(string key, StoredOAuthToken token, CancellationToken cancellationToken);
}
