using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Makes PingOne the source of RBAC. For a Ping (or, when configured, SAML) login it reads the user's group
/// membership through <see cref="PingManagementClient"/> and maps those groups onto FHIRBridge roles with
/// <see cref="PingGroupRoleMapper"/> (an admin-ordered list of group id/name to a single Segue role).
/// Only role names and counts are ever logged — no tokens, emails or group contents.
/// </summary>
public sealed class PingRoleResolver : IExternalRoleResolver
{
    private readonly PingSettingsReader _settings;
    private readonly PingManagementClient _client;
    private readonly ILogger<PingRoleResolver> _logger;

    public PingRoleResolver(PingSettingsReader settings, PingManagementClient client, ILogger<PingRoleResolver> logger)
    {
        _settings = settings;
        _client = client;
        _logger = logger;
    }

    public async Task<bool> AppliesToAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        var isPingLogin = identity.Provider == LoginProvider.Ping;
        var isSamlLogin = identity.Provider == LoginProvider.Saml && settings.ApplyToSaml;

        return settings.Enabled && (isPingLogin || isSamlLogin);
    }

    public async Task<ExternalRoleResolution> ResolveAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        PingSettings? settings = null;
        try
        {
            settings = await _settings.GetAsync(cancellationToken);
            var accessToken = await _client.GetManagementTokenAsync(settings, cancellationToken);

            var pingUserId = identity.Provider == LoginProvider.Ping
                ? identity.Subject
                : await _client.FindUserIdByEmailAsync(settings, accessToken, identity.Email, cancellationToken);

            if (string.IsNullOrWhiteSpace(pingUserId))
            {
                _logger.LogWarning("Ping role lookup: no PingOne user matched the signed-in identity.");
                return new ExternalRoleResolution(true, [], settings.JitProvisioning, settings.RoleSyncMode, settings.ConflictMode, settings.NoRoleMode);
            }

            var groupKeys = await _client.GetGroupKeysAsync(settings, accessToken, pingUserId, cancellationToken);
            var roles = PingGroupRoleMapper.MapInOrder(groupKeys, settings.GroupRoleMappings);

            _logger.LogInformation(
                "Ping role lookup: {GroupCount} group(s) mapped to {RoleCount} FHIRBridge role(s).",
                groupKeys.Count, roles.Count);

            return new ExternalRoleResolution(true, roles, settings.JitProvisioning, settings.RoleSyncMode, settings.ConflictMode, settings.NoRoleMode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ping role lookup failed; PingOne could not be queried.");
            return settings is null ? ExternalRoleResolution.Failed : ExternalRoleResolution.FailedFor(settings.RoleSyncMode);
        }
    }
}
