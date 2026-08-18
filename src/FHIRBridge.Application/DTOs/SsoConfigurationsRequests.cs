namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Everything the "SSO Configurations" admin screen shows. SAML/magic-link fields are live,
/// DB-backed (<c>SystemSetting</c> rows) — editable from the screen and effective without a restart.
/// Entra/Google fields are read-only status: changing those still requires an appsettings edit +
/// restart, since they're bound into the ASP.NET Core auth scheme at startup.
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
    bool GoogleEnabled);

/// <summary>Writes every SAML/magic-link field in one call — each persists as its own <c>SystemSetting</c> row.</summary>
public sealed record UpdateSsoConfigurationsRequest(
    bool SamlEnabled,
    string ServiceProviderEntityId,
    string IdentityProviderEntityId,
    string SingleSignOnUrl,
    string IdentityProviderCertificate,
    string PortalRedirectUrl,
    string PortalErrorRedirectUrl,
    bool MagicLinkEnabled);
