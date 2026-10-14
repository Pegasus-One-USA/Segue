using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
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

    // SystemSetting.Value is capped at 2000 characters.
    private const int MaxMappingsJsonLength = 1900;

    // Bare hostnames only: these are placed into URLs the server calls with credentials attached, so a scheme,
    // path, port or userinfo must never be accepted. The Management API host must be a PingOne one.
    private static readonly Regex HostnamePattern = new(
        @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PingOneHostPattern = new(
        @"^[a-z0-9-]+(\.[a-z0-9-]+)*\.pingone\.[a-z.]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ISystemSettingsCache _settingsCache;
    private readonly ISystemSettingsService _systemSettingsService;
    private readonly SamlAuthenticationOptions _samlFallback;
    private readonly LocalAuthOptions _localAuthFallback;
    private readonly EntraAuthenticationOptions _entraFallback;
    private readonly GoogleAuthenticationOptions _google;
    private readonly PingAuthenticationOptions _pingFallback;
    private readonly ISecretWriter _secretWriter;
    private readonly IPingConnectionTester _pingTester;

    public SsoConfigurationsService(
        ISystemSettingsCache settingsCache,
        ISystemSettingsService systemSettingsService,
        IOptions<SamlAuthenticationOptions> samlOptions,
        IOptions<LocalAuthOptions> localAuthOptions,
        IOptions<EntraAuthenticationOptions> entraOptions,
        IOptions<GoogleAuthenticationOptions> googleOptions,
        IOptions<PingAuthenticationOptions> pingOptions,
        ISecretWriter secretWriter,
        IPingConnectionTester pingTester)
    {
        _settingsCache = settingsCache;
        _systemSettingsService = systemSettingsService;
        _samlFallback = samlOptions.Value;
        _localAuthFallback = localAuthOptions.Value;
        _entraFallback = entraOptions.Value;
        _google = googleOptions.Value;
        _pingFallback = pingOptions.Value;
        _secretWriter = secretWriter;
        _pingTester = pingTester;
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
            GoogleEnabled: _google.Enabled,
            Ping: await GetPingAsync(cancellationToken));
    }

    public Task<ExternalConnectionTestResult> TestPingConnectionAsync(CancellationToken cancellationToken) =>
        _pingTester.TestAsync(cancellationToken);

    private async Task<PingConfigurationDto> GetPingAsync(CancellationToken cancellationToken)
    {
        var mappingsJson = await _settingsCache.GetStringAsync(PingSettingKeys.GroupRoleMappings, string.Empty, cancellationToken);
        var mappings = PingGroupRoleMapper.Parse(mappingsJson)
            ?? PingGroupRoleMapper.FromDictionary(_pingFallback.GroupRoleMappings);
        var syncMode = await _settingsCache.GetStringAsync(
            PingSettingKeys.RoleSyncMode, _pingFallback.RoleSyncMode.ToString(), cancellationToken);
        var conflictMode = await _settingsCache.GetStringAsync(
            PingSettingKeys.ConflictMode, _pingFallback.ConflictMode.ToString(), cancellationToken);
        var noMappedGroupMode = await _settingsCache.GetStringAsync(
            PingSettingKeys.NoMappedGroupMode, _pingFallback.NoMappedGroupMode.ToString(), cancellationToken);

        var secretConfigured = await _settingsCache.GetBoolAsync(PingSettingKeys.WorkerSecretConfigured, false, cancellationToken)
            || !string.IsNullOrWhiteSpace(_pingFallback.WorkerClientSecretName);

        return new PingConfigurationDto(
            await _settingsCache.GetBoolAsync(PingSettingKeys.Enabled, _pingFallback.Enabled, cancellationToken),
            await _settingsCache.GetStringAsync(PingSettingKeys.EnvironmentId, _pingFallback.EnvironmentId ?? string.Empty, cancellationToken),
            await _settingsCache.GetStringAsync(PingSettingKeys.AuthDomain, _pingFallback.AuthDomain, cancellationToken),
            await _settingsCache.GetStringAsync(PingSettingKeys.ApiDomain, _pingFallback.ApiDomain, cancellationToken),
            await _settingsCache.GetStringAsync(PingSettingKeys.ClientId, _pingFallback.ClientId ?? string.Empty, cancellationToken),
            await _settingsCache.GetStringAsync(PingSettingKeys.WorkerClientId, _pingFallback.WorkerClientId ?? string.Empty, cancellationToken),
            secretConfigured,
            await _settingsCache.GetBoolAsync(PingSettingKeys.JitProvisioning, _pingFallback.JitProvisioning, cancellationToken),
            await _settingsCache.GetBoolAsync(PingSettingKeys.ApplyToSaml, _pingFallback.ApplyToSaml, cancellationToken),
            mappings.Select(m => new PingGroupRoleMappingDto(m.Group, m.Role)).ToList(),
            secretConfigured ? await _pingTester.GetWorkerSecretHintAsync(cancellationToken) : null,
            syncMode,
            conflictMode,
            noMappedGroupMode);
    }

    private async Task UpdatePingAsync(UpdatePingConfigurationRequest ping, CancellationToken cancellationToken)
    {
        var environmentId = (ping.EnvironmentId ?? string.Empty).Trim();
        var authDomain = (ping.AuthDomain ?? string.Empty).Trim();
        var apiDomain = (ping.ApiDomain ?? string.Empty).Trim();

        if (ping.Enabled)
        {
            if (!Guid.TryParse(environmentId, out _))
            {
                throw new ArgumentException("Ping Environment ID must be a GUID.");
            }

            if (string.IsNullOrWhiteSpace(ping.ClientId))
            {
                throw new ArgumentException("Ping Client ID is required when Ping is enabled.");
            }
        }

        if (!string.IsNullOrEmpty(authDomain) && !HostnamePattern.IsMatch(authDomain))
        {
            throw new ArgumentException("Ping Auth Domain must be a bare host name, e.g. auth.pingone.com.");
        }

        if (!string.IsNullOrEmpty(apiDomain) && !PingOneHostPattern.IsMatch(apiDomain))
        {
            throw new ArgumentException("Ping API Domain must be a PingOne host name, e.g. api.pingone.com.");
        }

        // The list order IS the priority order (first match wins when conflicts are resolved by mapping order).
        var mappings = new List<PingGroupRoleMapping>();
        foreach (var mapping in ping.GroupRoleMappings ?? [])
        {
            var group = mapping.Group?.Trim();
            var role = mapping.Role?.Trim();
            if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(role))
            {
                throw new ArgumentException("Every group mapping needs a PingOne group and exactly one Segue role.");
            }

            if (mappings.Any(m => string.Equals(m.Group, group, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"The PingOne group '{group}' is mapped more than once.");
            }

            mappings.Add(new PingGroupRoleMapping(group, role));
        }

        ExternalRoleSyncMode? syncMode = null;
        if (ping.RoleSyncMode is not null)
        {
            if (!Enum.TryParse<ExternalRoleSyncMode>(ping.RoleSyncMode, ignoreCase: true, out var parsedSync))
            {
                throw new ArgumentException("Unknown role sync mode.");
            }

            syncMode = parsedSync;
        }

        ExternalRoleConflictMode? conflictMode = null;
        if (ping.ConflictMode is not null)
        {
            if (!Enum.TryParse<ExternalRoleConflictMode>(ping.ConflictMode, ignoreCase: true, out var parsedConflict))
            {
                throw new ArgumentException("Unknown role conflict mode.");
            }

            conflictMode = parsedConflict;
        }

        ExternalNoRoleMode? noRoleMode = null;
        if (ping.NoMappedGroupMode is not null)
        {
            if (!Enum.TryParse<ExternalNoRoleMode>(ping.NoMappedGroupMode, ignoreCase: true, out var parsedNoRole))
            {
                throw new ArgumentException("Unknown no-mapped-group mode.");
            }

            noRoleMode = parsedNoRole;
        }

        var mappingsJson = PingGroupRoleMapper.Serialize(mappings);
        if (mappingsJson.Length > MaxMappingsJsonLength)
        {
            throw new ArgumentException("Too many group mappings to store; remove some and try again.");
        }

        if (!string.IsNullOrWhiteSpace(ping.WorkerClientSecret))
        {
            // Trimmed: a stray space or newline from copy/paste is the commonest cause of a rejected secret.
            await _secretWriter.WriteSecretAsync(PingSettingKeys.UiWorkerSecretReference, ping.WorkerClientSecret.Trim(), cancellationToken);
            await _systemSettingsService.SetAsync(
                PingSettingKeys.WorkerSecretConfigured, bool.TrueString,
                "Whether the PingOne worker secret was set on the SSO Configurations screen (the secret itself is in the secret store).",
                cancellationToken);
        }

        await _systemSettingsService.SetAsync(
            PingSettingKeys.Enabled, ping.Enabled.ToString(), "PingOne SSO and role lookup on/off — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.EnvironmentId, environmentId, "PingOne environment id — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.AuthDomain, authDomain, "PingOne authorization host — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.ApiDomain, apiDomain, "PingOne Management API host — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.ClientId, (ping.ClientId ?? string.Empty).Trim(), "PingOne portal application client id — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.WorkerClientId, (ping.WorkerClientId ?? string.Empty).Trim(), "PingOne worker application client id — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.JitProvisioning, ping.JitProvisioning.ToString(), "Create accounts for new Ping users with a mapped role — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.ApplyToSaml, ping.ApplyToSaml.ToString(), "Apply Ping role lookup to SAML logins — SSO Configurations screen.", cancellationToken);
        await _systemSettingsService.SetAsync(
            PingSettingKeys.GroupRoleMappings, mappingsJson, "PingOne group to Segue role mappings, in priority order (JSON) — SSO Configurations screen.", cancellationToken);
        if (syncMode is not null)
        {
            await _systemSettingsService.SetAsync(
                PingSettingKeys.RoleSyncMode, syncMode.Value.ToString(), "What to do when the PingOne role and the Segue role differ — SSO Configurations screen.", cancellationToken);
        }

        if (noRoleMode is not null)
        {
            await _systemSettingsService.SetAsync(
                PingSettingKeys.NoMappedGroupMode, noRoleMode.Value.ToString(),
                "What to do when none of a PingOne user's groups map to a Segue role — SSO Configurations screen.", cancellationToken);
        }

        if (conflictMode is not null)
        {
            await _systemSettingsService.SetAsync(
                PingSettingKeys.ConflictMode, conflictMode.Value.ToString(), "What to do when PingOne groups map to several Segue roles — SSO Configurations screen.", cancellationToken);
        }
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

        if (request.Ping is not null)
        {
            await UpdatePingAsync(request.Ping, cancellationToken);
        }

        return await GetAsync(cancellationToken);
    }
}
