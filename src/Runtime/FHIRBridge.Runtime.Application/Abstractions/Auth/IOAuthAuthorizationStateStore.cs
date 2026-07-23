using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

/// <summary>
/// Holds the short-lived state of an in-flight interactive OAuth sign-in between the authorize redirect and the
/// callback. It is keyed by the unguessable <c>state</c> value (which also defends against CSRF), and entries are
/// single-use: <see cref="TakeAsync"/> returns and removes the entry so a <c>state</c> cannot be replayed.
/// </summary>
public interface IOAuthAuthorizationStateStore
{
    Task SaveAsync(string state, PendingAuthorization pending, CancellationToken cancellationToken);

    /// <summary>Returns and removes the pending authorization for a state, or null if unknown/already consumed.</summary>
    Task<PendingAuthorization?> TakeAsync(string state, CancellationToken cancellationToken);
}

/// <summary>
/// Everything the callback needs to complete a sign-in: which source connection is being authorized, the PKCE code
/// verifier and the exact redirect URI used at authorize time (both must match on the token exchange), and the
/// token endpoint / client id to exchange against.
/// </summary>
public sealed record PendingAuthorization(
    Guid SourceConnectionId,
    RuntimeSourceType SourceType,
    string SourceName,
    string CodeVerifier,
    string RedirectUri,
    string TokenEndpoint,
    string ClientId,
    Guid? RouteId = null,
    Guid? WorkflowId = null,
    // The base URL actually used to issue this authorization (the source connection's own, or an EhrEndpoint
    // override), carried through to the callback so the token exchange/save records the same value — the token
    // exchange builds its own FhirSourceConfiguration from scratch and has no other way to know which URL was used.
    string? ResolvedBaseUrl = null,
    // True only for an EHR launch (a non-null `launch` token was issued alongside the authorize request), meaning
    // Epic itself established a patient context before the redirect. False for Standalone/patient-standalone
    // sign-ins, which have no upfront patient context at all — the callback uses this to decide whether its own
    // auto-triggered convenience run has any chance of succeeding (see CompleteAsync).
    bool HasLaunchContext = false,
    // The caller-supplied redirect URL (e.g. the third-party app that requested this launch URL), captured at mint
    // time and carried here via the encrypted launch-context token — preferred over the source connection's static
    // PostLaunchRedirectUri when present. Lives only in this cache-backed record, never persisted to SQL.
    string? CallerId = null,
    // An opaque, caller-supplied (or FHIRBridge-minted) session identifier — distinct from CallerId above, which is
    // a redirect URL and never used for caching. This is what the Patient Standalone interactive token cache keys
    // on instead of SourceConnectionId (see SmartAuthorizationCodeTokenProvider.BuildStoreKey), so every pipeline
    // sharing this same real patient's session reuses the one token their authorization already covers.
    string? SessionId = null);
