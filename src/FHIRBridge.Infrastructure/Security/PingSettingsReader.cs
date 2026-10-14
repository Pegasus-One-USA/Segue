using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>Point-in-time view of the PingOne settings.</summary>
public sealed record PingSettings(
    bool Enabled,
    string EnvironmentId,
    string AuthDomain,
    string ApiDomain,
    string ClientId,
    string WorkerClientId,
    SecretReference? WorkerSecretReference,
    bool JitProvisioning,
    bool ApplyToSaml,
    ExternalRoleSyncMode RoleSyncMode,
    ExternalRoleConflictMode ConflictMode,
    ExternalNoRoleMode NoRoleMode,
    IReadOnlyList<PingGroupRoleMapping> GroupRoleMappings)
{
    /// <summary>OIDC issuer/authority root, e.g. <c>https://auth.pingone.com/{envId}/as</c>.</summary>
    public string Authority => $"https://{AuthDomain.Trim().TrimEnd('/')}/{EnvironmentId}/as";

    /// <summary>Management API root for this environment.</summary>
    public string ManagementApiBase => $"https://{ApiDomain.Trim().TrimEnd('/')}/v1/environments/{EnvironmentId}";
}

/// <summary>
/// Reads the PingOne settings live from <see cref="ISystemSettingsCache"/> (SSO Configurations screen),
/// falling back to the appsettings value — the same live-read pattern as <see cref="EntraTokenValidator"/>,
/// so a change saved from the screen takes effect on the next sign-in with no restart or redeploy.
/// </summary>
public sealed class PingSettingsReader
{
    private readonly PingAuthenticationOptions _fallback;
    private readonly ISystemSettingsCache _settingsCache;

    public PingSettingsReader(IOptions<PingAuthenticationOptions> options, ISystemSettingsCache settingsCache)
    {
        _fallback = options.Value;
        _settingsCache = settingsCache;
    }

    public async Task<PingSettings> GetAsync(CancellationToken cancellationToken)
    {
        var enabled = await _settingsCache.GetBoolAsync(PingSettingKeys.Enabled, _fallback.Enabled, cancellationToken);
        var environmentId = await _settingsCache.GetStringAsync(
            PingSettingKeys.EnvironmentId, _fallback.EnvironmentId ?? string.Empty, cancellationToken);
        var authDomain = await _settingsCache.GetStringAsync(PingSettingKeys.AuthDomain, _fallback.AuthDomain, cancellationToken);
        var apiDomain = await _settingsCache.GetStringAsync(PingSettingKeys.ApiDomain, _fallback.ApiDomain, cancellationToken);
        var clientId = await _settingsCache.GetStringAsync(PingSettingKeys.ClientId, _fallback.ClientId ?? string.Empty, cancellationToken);
        var workerClientId = await _settingsCache.GetStringAsync(
            PingSettingKeys.WorkerClientId, _fallback.WorkerClientId ?? string.Empty, cancellationToken);
        var jit = await _settingsCache.GetBoolAsync(PingSettingKeys.JitProvisioning, _fallback.JitProvisioning, cancellationToken);
        var applyToSaml = await _settingsCache.GetBoolAsync(PingSettingKeys.ApplyToSaml, _fallback.ApplyToSaml, cancellationToken);

        // A secret entered on the screen wins; otherwise fall back to the vault/secret named in appsettings.
        var secretFromUi = await _settingsCache.GetBoolAsync(PingSettingKeys.WorkerSecretConfigured, false, cancellationToken);
        var secretReference = secretFromUi
            ? PingSettingKeys.UiWorkerSecretReference
            : !string.IsNullOrWhiteSpace(_fallback.WorkerClientSecretKeyVaultName) &&
              !string.IsNullOrWhiteSpace(_fallback.WorkerClientSecretName)
                ? new SecretReference(_fallback.WorkerClientSecretKeyVaultName, _fallback.WorkerClientSecretName)
                : null;

        var mappingsJson = await _settingsCache.GetStringAsync(PingSettingKeys.GroupRoleMappings, string.Empty, cancellationToken);
        var modeText = await _settingsCache.GetStringAsync(PingSettingKeys.RoleSyncMode, _fallback.RoleSyncMode.ToString(), cancellationToken);
        var conflictText = await _settingsCache.GetStringAsync(PingSettingKeys.ConflictMode, _fallback.ConflictMode.ToString(), cancellationToken);
        var noRoleText = await _settingsCache.GetStringAsync(
            PingSettingKeys.NoMappedGroupMode, _fallback.NoMappedGroupMode.ToString(), cancellationToken);

        // An unrecognised saved value falls back to the strictest choice, never the loosest.
        var mode = Enum.TryParse<ExternalRoleSyncMode>(modeText, ignoreCase: true, out var parsedMode)
            ? parsedMode
            : ExternalRoleSyncMode.RestrictMismatch;
        var conflict = Enum.TryParse<ExternalRoleConflictMode>(conflictText, ignoreCase: true, out var parsedConflict)
            ? parsedConflict
            : ExternalRoleConflictMode.Deny;
        var noRole = Enum.TryParse<ExternalNoRoleMode>(noRoleText, ignoreCase: true, out var parsedNoRole)
            ? parsedNoRole
            : ExternalNoRoleMode.Deny;

        return new PingSettings(
            enabled, environmentId, authDomain, apiDomain, clientId, workerClientId,
            secretReference, jit, applyToSaml, mode, conflict, noRole, ParseMappings(mappingsJson));
    }

    private IReadOnlyList<PingGroupRoleMapping> ParseMappings(string json) =>
        PingGroupRoleMapper.Parse(json) ?? PingGroupRoleMapper.FromDictionary(_fallback.GroupRoleMappings);
}
