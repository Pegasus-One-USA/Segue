using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Application.Services;

public sealed class SsoConfigurationsService : ISsoConfigurationsService
{
    private const string SamlEnabledKey = "Authentication:Saml:Enabled";
    private const string ServiceProviderEntityIdKey = "Authentication:Saml:ServiceProviderEntityId";
    private const string IdentityProviderEntityIdKey = "Authentication:Saml:IdentityProviderEntityId";
    private const string SingleSignOnUrlKey = "Authentication:Saml:SingleSignOnUrl";
    private const string IdentityProviderCertificateKey = "Authentication:Saml:IdentityProviderCertificate";
    private const string PortalRedirectUrlKey = "Authentication:Saml:PortalRedirectUrl";
    private const string PortalErrorRedirectUrlKey = "Authentication:Saml:PortalErrorRedirectUrl";
    private const string MagicLinkEnabledKey = "LocalAuth:MagicLink:Enabled";
    private const string EntraEnabledKey = "Authentication:Entra:Enabled";
    private const string EntraInstanceKey = "Authentication:Entra:Instance";
    private const string EntraTenantIdKey = "Authentication:Entra:TenantId";
    private const string EntraClientIdKey = "Authentication:Entra:ClientId";

    private readonly ISystemSettingsCache _settingsCache;
    private readonly ISystemSettingsService _systemSettingsService;
    private readonly SamlAuthenticationOptions _samlFallback;
    private readonly LocalAuthOptions _localAuthFallback;
    private readonly EntraAuthenticationOptions _entraFallback;
    private readonly GoogleAuthenticationOptions _google;

    public SsoConfigurationsService(
        ISystemSettingsCache settingsCache,
        ISystemSettingsService systemSettingsService,
        IOptions<SamlAuthenticationOptions> samlOptions,
        IOptions<LocalAuthOptions> localAuthOptions,
        IOptions<EntraAuthenticationOptions> entraOptions,
        IOptions<GoogleAuthenticationOptions> googleOptions)
    {
        _settingsCache = settingsCache;
        _systemSettingsService = systemSettingsService;
        _samlFallback = samlOptions.Value;
        _localAuthFallback = localAuthOptions.Value;
        _entraFallback = entraOptions.Value;
        _google = googleOptions.Value;
    }

    public async Task<SsoConfigurationsDto> GetAsync(CancellationToken cancellationToken)
    {
        var samlEnabled = await _settingsCache.GetBoolAsync(SamlEnabledKey, _samlFallback.Enabled, cancellationToken);
        var serviceProviderEntityId = await _settingsCache.GetStringAsync(
            ServiceProviderEntityIdKey, _samlFallback.ServiceProviderEntityId ?? string.Empty, cancellationToken);
        var identityProviderEntityId = await _settingsCache.GetStringAsync(
            IdentityProviderEntityIdKey, _samlFallback.IdentityProviderEntityId ?? string.Empty, cancellationToken);
        var singleSignOnUrl = await _settingsCache.GetStringAsync(
            SingleSignOnUrlKey, _samlFallback.SingleSignOnUrl ?? string.Empty, cancellationToken);
        var identityProviderCertificate = await _settingsCache.GetStringAsync(
            IdentityProviderCertificateKey, _samlFallback.IdentityProviderCertificate ?? string.Empty, cancellationToken);
        var portalRedirectUrl = await _settingsCache.GetStringAsync(
            PortalRedirectUrlKey, _samlFallback.PortalRedirectUrl ?? string.Empty, cancellationToken);
        var portalErrorRedirectUrl = await _settingsCache.GetStringAsync(
            PortalErrorRedirectUrlKey, _samlFallback.PortalErrorRedirectUrl ?? string.Empty, cancellationToken);
        var magicLinkEnabled = await _settingsCache.GetBoolAsync(
            MagicLinkEnabledKey, _localAuthFallback.MagicLink.Enabled, cancellationToken);

        var entraEnabled = await _settingsCache.GetBoolAsync(EntraEnabledKey, _entraFallback.Enabled, cancellationToken);
        var entraInstance = await _settingsCache.GetStringAsync(
            EntraInstanceKey, _entraFallback.Instance ?? "https://login.microsoftonline.com/", cancellationToken);
        var entraTenantId = await _settingsCache.GetStringAsync(
            EntraTenantIdKey, _entraFallback.TenantId ?? string.Empty, cancellationToken);
        var entraClientId = await _settingsCache.GetStringAsync(
            EntraClientIdKey, _entraFallback.ClientId ?? string.Empty, cancellationToken);

        return new SsoConfigurationsDto(
            samlEnabled,
            serviceProviderEntityId,
            identityProviderEntityId,
            singleSignOnUrl,
            identityProviderCertificate,
            portalRedirectUrl,
            portalErrorRedirectUrl,
            magicLinkEnabled,
            SamlMetadataUrl: string.Empty, // computed by the controller, which has the request's own scheme/host
            SamlAcsUrl: string.Empty,
            entraEnabled,
            entraInstance,
            entraTenantId,
            entraClientId,
            GoogleEnabled: _google.Enabled);
    }

    public async Task<SsoConfigurationsDto> UpdateAsync(UpdateSsoConfigurationsRequest request, CancellationToken cancellationToken)
    {
        await _systemSettingsService.SetAsync(
            SamlEnabledKey, request.SamlEnabled.ToString(), "SAML 2.0 SSO on/off — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            ServiceProviderEntityIdKey, request.ServiceProviderEntityId ?? string.Empty,
            "This app's own SAML SP entity id — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            IdentityProviderEntityIdKey, request.IdentityProviderEntityId ?? string.Empty,
            "The IdP's entity id — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            SingleSignOnUrlKey, request.SingleSignOnUrl ?? string.Empty,
            "The IdP's SSO redirect endpoint — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            IdentityProviderCertificateKey, request.IdentityProviderCertificate ?? string.Empty,
            "The IdP's base64 X.509 signing certificate (public) — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PortalRedirectUrlKey, request.PortalRedirectUrl ?? string.Empty,
            "Where a successful SAML login redirects to — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PortalErrorRedirectUrlKey, request.PortalErrorRedirectUrl ?? string.Empty,
            "Where a failed SAML login redirects to — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            MagicLinkEnabledKey, request.MagicLinkEnabled.ToString(),
            "Passwordless email sign-in link on/off — SSO Configurations screen.", cancellationToken);

        await _systemSettingsService.SetAsync(
            EntraEnabledKey, request.EntraEnabled.ToString(),
            "Microsoft Entra ID \"Continue with Microsoft\" login on/off — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            EntraInstanceKey, request.EntraInstance ?? string.Empty,
            "Entra login instance URL — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            EntraTenantIdKey, request.EntraTenantId ?? string.Empty,
            "Entra directory (tenant) id — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            EntraClientIdKey, request.EntraClientId ?? string.Empty,
            "Entra application (client) id — SSO Configurations screen.", cancellationToken);

        return await GetAsync(cancellationToken);
    }
}
