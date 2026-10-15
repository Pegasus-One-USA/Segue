using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Application.Services;

/// <summary>What the external identity provider decided about a sign-in, beyond the user it ended up with.</summary>
/// <param name="User">The user to sign in: the one passed in, or a just-in-time created one. Null when none exists.</param>
/// <param name="LookupSucceeded">True when an identity provider was asked and answered.</param>
/// <param name="HasMappedRole">True when one of the identity's groups maps to an existing Segue role.</param>
public sealed record ExternalRoleOutcome(User? User, bool LookupSucceeded, bool HasMappedRole);

/// <summary>
/// Applies the role an external identity provider (e.g. PingOne) grants to a signing-in user: fetches it live,
/// resolves group conflicts, creates the account when just-in-time provisioning allows, and reconciles an existing
/// user's Segue role according to the configured mode. Throws <see cref="UnauthorizedAccessException"/> when the
/// sign-in must be refused.
/// </summary>
public interface IExternalRoleReconciler
{
    Task<ExternalRoleOutcome> ReconcileAsync(
        User? user, ExternalIdentity identity, string authenticationType, CancellationToken cancellationToken);
}

public sealed class ExternalRoleReconciler : IExternalRoleReconciler
{
    private readonly IUserAccessRepository _repository;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly IEnumerable<IExternalRoleResolver> _roleResolvers;
    private readonly IUserPermissionsProvider _permissionsProvider;
    private readonly ILogger<ExternalRoleReconciler> _logger;

    public ExternalRoleReconciler(
        IUserAccessRepository repository,
        IGovernanceLogger governanceLogger,
        IEnumerable<IExternalRoleResolver> roleResolvers,
        IUserPermissionsProvider permissionsProvider,
        ILogger<ExternalRoleReconciler>? logger = null)
    {
        _repository = repository;
        _governanceLogger = governanceLogger;
        _roleResolvers = roleResolvers;
        _permissionsProvider = permissionsProvider;
        _logger = logger ?? NullLogger<ExternalRoleReconciler>.Instance;
    }

    public async Task<ExternalRoleOutcome> ReconcileAsync(
        User? user, ExternalIdentity identity, string authenticationType, CancellationToken cancellationToken)
    {
        // The IdP's roles are fetched live on every login.
        var resolver = await FindRoleResolverAsync(identity, cancellationToken);
        ExternalRoleResolution? resolution = null;
        var mappedRoles = new List<Role>();
        if (resolver is not null)
        {
            resolution = await resolver.ResolveAsync(identity, cancellationToken);
            if (resolution.Succeeded)
            {
                foreach (var roleName in resolution.RoleNames)
                {
                    var role = await _repository.GetRoleByNameAsync(roleName, cancellationToken);
                    if (role is null)
                    {
                        _logger.LogWarning(
                            "External role mapping references unknown FHIRBridge role {RoleName}; ignored.", roleName);
                        continue;
                    }

                    mappedRoles.Add(role);
                }
            }
        }

        // A Segue user holds ONE role. Several DIFFERENT mapped roles is ambiguous: either refuse the sign-in or let the
        // admin-ordered mapping decide (mappedRoles is in mapping order, so the first entry is the top-priority match).
        // Groups with no mapping are ignored and never cause this.
        if (resolution is { Succeeded: true, ConflictMode: ExternalRoleConflictMode.Deny } && mappedRoles.Count > 1)
        {
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry(
                    authenticationType, Success: false, identity.Email,
                    "The identity-provider groups map to more than one Segue role: " +
                    string.Join(", ", mappedRoles.Select(r => r.Name))),
                cancellationToken);
            throw new UnauthorizedAccessException(
                $"Your {ProviderLabel(identity.Provider)} groups are linked to more than one Segue role, so sign-in is blocked. "
                + "Ask an administrator to fix the group mapping.");
        }

        var targetRole = mappedRoles.Count > 0 ? mappedRoles[0] : null;

        if (user is null && resolution is { Succeeded: true, AllowProvisioning: true } && targetRole is not null)
        {
            // JIT provisioning: first sign-in of an identity whose IdP group maps to a Segue role.
            user = new User(identity.Subject, identity.Email, identity.Name, SeededSecurityIds.DefaultTenantId);
            user.LinkExternalIdentity(identity.Subject, identity.Provider);
            user.RecordRoleSync($"Created via {identity.Provider} SSO on {DateTime.UtcNow:yyyy-MM-dd} with role {targetRole.Name}");
            await _repository.AddUserAsync(user, cancellationToken);
            await _repository.SetUserRolesAsync(user.Id, [targetRole.Id], cancellationToken);
            _permissionsProvider.Invalidate(user.Id);

            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry(
                    $"{authenticationType}:Provisioned", Success: true, identity.Email,
                    $"Account created via {identity.Provider} SSO with role {targetRole.Name}."),
                cancellationToken);
        }
        else if (user is not null && user.IsEnabled && resolution is not null)
        {
            await ApplyExternalRoleAsync(user, identity, authenticationType, resolution, targetRole, cancellationToken);
        }

        return new ExternalRoleOutcome(user, resolution is { Succeeded: true }, targetRole is not null);
    }

    /// <summary>
    /// Reconciles an existing user's Segue role with the one the external IdP maps, according to the configured mode.
    /// Throws <see cref="UnauthorizedAccessException"/> when the sign-in must be refused.
    /// </summary>
    private async Task ApplyExternalRoleAsync(
        User user,
        ExternalIdentity identity,
        string authenticationType,
        ExternalRoleResolution resolution,
        Role? targetRole,
        CancellationToken cancellationToken)
    {
        if (!resolution.Succeeded)
        {
            if (resolution.Mode == ExternalRoleSyncMode.RestrictMismatch)
            {
                // Cannot verify the role, and this mode exists to refuse a mismatch: do not fall open.
                await DenyAsync(
                    identity, authenticationType, "The identity provider could not be reached to verify the user's role.",
                    $"Your role could not be verified with {ProviderLabel(identity.Provider)} right now. Please try again shortly.", cancellationToken);
            }

            // Otherwise an IdP outage must not lock existing users out: they keep their last roles.
            _logger.LogWarning(
                LogEvents.LoginFailed,
                "Role lookup failed for {AuthenticationType}; keeping previously synced roles for user {UserId}.",
                authenticationType, user.Id);
            return;
        }

        if (targetRole is null)
        {
            if (resolution.NoRoleMode == ExternalNoRoleMode.AllowSegueRole)
            {
                return; // The admin allows it: the user signs in with the role they already hold in Segue, untouched.
            }

            await DenyAsync(
                identity, authenticationType, "No identity-provider role maps to a Segue role.",
                $"Your {ProviderLabel(identity.Provider)} account is not in any group that is linked to a Segue role. "
                + "Ask an administrator to add you to a mapped group.", cancellationToken);
            return;
        }

        if (resolution.Mode == ExternalRoleSyncMode.AllowDifferent)
        {
            return;
        }

        var currentRoles = await _repository.GetUserRolesAsync(user.Id, cancellationToken);
        var matches = currentRoles.Count == 1 && currentRoles[0].Id == targetRole.Id;
        if (matches)
        {
            return;
        }

        var currentNames = currentRoles.Count == 0 ? "no role" : string.Join(", ", currentRoles.Select(r => r.Name));

        if (resolution.Mode == ExternalRoleSyncMode.RestrictMismatch)
        {
            await DenyAsync(
                identity, authenticationType,
                $"The user's Segue role ({currentNames}) does not match the identity-provider role ({targetRole.Name}).",
                $"Your Segue role ({currentNames}) does not match your {ProviderLabel(identity.Provider)} role ({targetRole.Name}). "
                + "Ask an administrator to align them.",
                cancellationToken);
            return;
        }

        // AutoSwitch: the identity provider's role wins, and the change is recorded on the user and in the audit log.
        var note = $"Role updated via {identity.Provider} SSO on {DateTime.UtcNow:yyyy-MM-dd}: {currentNames} to {targetRole.Name}";
        await _repository.SetUserRolesAsync(user.Id, [targetRole.Id], cancellationToken);
        user.RecordRoleSync(note);
        await _repository.UpdateUserAsync(user, cancellationToken);
        _permissionsProvider.Invalidate(user.Id);

        await _governanceLogger.LogAuthenticationAsync(
            new AuthenticationEntry($"{authenticationType}:RoleUpdated", Success: true, user.Email, note),
            cancellationToken);
    }

    /// <summary>The name of the identity provider as a user knows it, for error messages.</summary>
    internal static string ProviderLabel(LoginProvider provider) =>
        provider == LoginProvider.Ping ? "PingOne" : "identity provider";

    private async Task DenyAsync(
        ExternalIdentity identity,
        string authenticationType,
        string auditReason,
        string userMessage,
        CancellationToken cancellationToken)
    {
        await _governanceLogger.LogAuthenticationAsync(
            new AuthenticationEntry(authenticationType, Success: false, identity.Email, auditReason),
            cancellationToken);
        throw new UnauthorizedAccessException(userMessage);
    }

    private async Task<IExternalRoleResolver?> FindRoleResolverAsync(
        ExternalIdentity identity, CancellationToken cancellationToken)
    {
        foreach (var resolver in _roleResolvers)
        {
            if (await resolver.AppliesToAsync(identity, cancellationToken))
            {
                return resolver;
            }
        }

        return null;
    }
}
