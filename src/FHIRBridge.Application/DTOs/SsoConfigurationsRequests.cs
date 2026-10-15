namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Everything the "SSO Configurations" admin screen shows. SAML, magic-link, and Entra's login-flow
/// fields (Enabled/Instance/TenantId/ClientId) are live, DB-backed (<c>SystemSetting</c> rows) —
/// editable from the screen and effective without a restart for the "Continue with Microsoft" login
/// path. Google stays read-only status: changing it still requires an appsettings edit + restart.
/// (Entra's separate JWT bearer scheme, used only if a caller presents a raw Entra token directly as
/// an API Authorization header, is unaffected by this screen and still needs a restart regardless.)
/// </summary>
public sealed record SsoConfigurationsDto(
    bool SamlEnabled,
    string ServiceProviderEntityId,
    string IdentityProviderEntityId,
    string SingleSignOnUrl,
    string IdentityProviderCertificate,
    string PortalRedirectUrl,
    string PortalErrorRedirectUrl,
    bool MagicLinkEnabled,
    string SamlMetadataUrl,
    string SamlAcsUrl,
    bool EntraEnabled,
    string EntraInstance,
    string EntraTenantId,
    string EntraClientId,
    bool GoogleEnabled,
    PingConfigurationDto? Ping = null);

/// <summary>One PingOne group (id or display name) and the single Segue role it grants. The list order is the priority order.</summary>
public sealed record PingGroupRoleMappingDto(string Group, string Role);

/// <summary>PingOne settings as shown on the screen. The worker client secret is write-only: only whether one is set is returned.</summary>
public sealed record PingConfigurationDto(
    bool Enabled,
    string EnvironmentId,
    string AuthDomain,
    string ApiDomain,
    string ClientId,
    string WorkerClientId,
    bool WorkerSecretConfigured,
    bool JitProvisioning,
    bool ApplyToSaml,
    IReadOnlyList<PingGroupRoleMappingDto> GroupRoleMappings,
    string? WorkerSecretHint = null,
    string RoleSyncMode = "AutoSwitch",
    string ConflictMode = "Deny",
    string NoMappedGroupMode = "Deny");

/// <summary>Writes every SAML/magic-link/Entra field in one call — each persists as its own <c>SystemSetting</c> row.</summary>
public sealed record UpdateSsoConfigurationsRequest(
    bool SamlEnabled,
    string ServiceProviderEntityId,
    string IdentityProviderEntityId,
    string SingleSignOnUrl,
    string IdentityProviderCertificate,
    string PortalRedirectUrl,
    string PortalErrorRedirectUrl,
    bool MagicLinkEnabled,
    bool EntraEnabled,
    string EntraInstance,
    string EntraTenantId,
    string EntraClientId,
    UpdatePingConfigurationRequest? Ping = null);

/// <summary>
/// PingOne settings to save. <paramref name="WorkerClientSecret"/> is optional: blank keeps the secret already
/// stored, a value replaces it (stored through the secret store, never in a settings row).
/// </summary>
public sealed record UpdatePingConfigurationRequest(
    bool Enabled,
    string EnvironmentId,
    string AuthDomain,
    string ApiDomain,
    string ClientId,
    string WorkerClientId,
    string? WorkerClientSecret,
    bool JitProvisioning,
    bool ApplyToSaml,
    IReadOnlyList<PingGroupRoleMappingDto> GroupRoleMappings,
    string? RoleSyncMode = null,
    string? ConflictMode = null,
    string? NoMappedGroupMode = null);
