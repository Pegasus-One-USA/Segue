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
    string? Patient = null);
