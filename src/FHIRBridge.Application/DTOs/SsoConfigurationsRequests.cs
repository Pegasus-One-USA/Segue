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
    bool GoogleEnabled);

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
    string EntraClientId);
