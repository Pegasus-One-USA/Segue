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
    Guid TenantId,
    Guid SourceConnectionId,
    RuntimeSourceType SourceType,
    string SourceName,
    string CodeVerifier,
    string RedirectUri,
    string TokenEndpoint,
    string ClientId);
