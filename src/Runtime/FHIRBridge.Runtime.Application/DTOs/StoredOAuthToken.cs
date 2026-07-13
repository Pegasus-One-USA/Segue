namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>
/// A previously acquired OAuth 2.0 token persisted out of band of a pipeline run — for grants that require an
/// interactive sign-in (authorization-code + PKCE) the token cannot be minted on demand, so the callback that
/// completes the sign-in stashes it here and the token provider reads (and silently refreshes) it later.
/// </summary>
public sealed record StoredOAuthToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresOnUtc,
    string? Scope = null,
    string? Patient = null,
    // The token endpoint the token was minted at. Interactive sources discover this from the source's
    // .well-known/smart-configuration at sign-in time rather than persisting it on the source connection, so it is
    // stashed with the token to make a later silent refresh possible without re-discovery.
    string? TokenEndpoint = null,
    // The FHIR base URL this session's launch actually resolved to — the source connection's own configured base
    // URL, or a hospital/organization EhrEndpoint override when the launch carried one. A later, separately
    // triggered workflow run reads this back (GetResolvedBaseUrlAsync) so it searches against the SAME hospital's
    // endpoint this session's login established, without needing to be told which hospital again.
    string? ResolvedBaseUrl = null);
