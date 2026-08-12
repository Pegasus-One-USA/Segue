namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Request to exercise the SMART Backend Services (client_credentials + private_key_jwt) exchange against Epic's
/// token endpoint, using a signing key that has already been provisioned into the secret store — pre-create, so the
/// source-connection wizard can show which scopes an Epic app is actually granted before the connection is saved.
/// </summary>
public sealed record BackendAuthScopesRequest(
    string TokenEndpoint,
    string ClientId,
    string? KeyId,
    string PrivateKeyVaultName,
    string PrivateKeySecretName,
    string? Scope);

/// <summary>
/// Result of a backend-services auth probe. <see cref="GrantedScopes"/> is Epic's actual <c>scope</c> response —
/// what the app is really allowed, which can differ from what was requested. Never carries the access token itself.
/// </summary>
public sealed record BackendAuthScopesResult(
    bool Success,
    IReadOnlyList<string> GrantedScopes,
    string? Error);
