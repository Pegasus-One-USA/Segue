namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Request to exercise the SMART Backend Services (client_credentials) exchange against a source's token endpoint
/// before the connection is saved, so the wizard can show whether the configured credentials actually work and
/// which scopes were granted, rather than only discovering a bad client id/secret/key once a whole workflow is
/// built and run. Covers both of FHIRBridge's Backend System auth methods, selected via <see cref="AuthMethod"/>:
///  - <c>"jwt"</c>: private_key_jwt, signing with a key already provisioned into the secret store (pre-create —
///    Generate/Import provisions the key immediately, before the connection itself is saved). Requires
///    <see cref="PrivateKeyVaultName"/> and <see cref="PrivateKeySecretName"/>.
///  - <c>"secret"</c>: a plain OAuth2 client secret (e.g. athenahealth). Unlike the signing key, nothing
///    provisions the secret into the secret store until the connection is saved, so it travels directly in
///    <see cref="ClientSecret"/> for this one-off, immediately-discarded token exchange — the same trust boundary
///    it already crosses (browser → this same authenticated API) when the connection is saved moments later.
/// </summary>
public sealed record BackendAuthScopesRequest(
    string TokenEndpoint,
    string ClientId,
    string AuthMethod,
    string? KeyId,
    string? PrivateKeyVaultName,
    string? PrivateKeySecretName,
    string? ClientSecret,
    string? AuthPlacement,
    string? Scope,
    /// <summary>
    /// The source's vendor (a <c>SourceSystemType</c> name, e.g. <c>"Healow"</c>), when known. Vendors registered
    /// in <c>VendorScopeCatalog</c> do not accept the generic <c>system/{Resource}.{read|rs}</c> vocabulary — eCW's
    /// only system wildcard is <c>system/*.r</c>, and it fails the WHOLE token request with <c>invalid_scope</c> on
    /// one unrecognized scope — so the probe respells <see cref="Scope"/> for that vendor rather than testing
    /// credentials with a scope string the server could never accept. Null (or an unregistered vendor) keeps
    /// <see cref="Scope"/> exactly as sent.
    /// </summary>
    string? Vendor = null);

/// <summary>
/// Result of a backend-services auth probe. <see cref="GrantedScopes"/> is the source's actual <c>scope</c>
/// response — what the app is really allowed, which can differ from what was requested. Never carries the access
/// token itself, nor (for the "secret" method) the client secret.
/// </summary>
public sealed record BackendAuthScopesResult(
    bool Success,
    IReadOnlyList<string> GrantedScopes,
    string? Error);
