using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Application.Services;

// SetupService runs before any tenant context exists (anonymous, empty database) — both first-run paths
// below assign the well-known Default Tenant (SeededSecurityIds.DefaultTenantId), the same tenant every
// pre-existing user was migrated into by the AddTenant migration. There is deliberately no "pick a tenant"
// UI on first-run setup; a deployment that genuinely needs more than one tenant creates the rest via the
// Tenant Management screen afterward.

/// <summary>
/// First-run setup. "Requires setup" is simply "no user exists yet". Creating the first SuperAdmin reuses the normal
/// local-user creation + login paths, then signs the caller in. The empty-database guard is re-checked here so the
/// endpoint is self-guarding even though it is anonymous. Also saves the outbound SMTP configuration collected on
/// the same screen (forced enabled) and requires Terms &amp; Conditions acceptance — this deployment ships as a
/// package with no separate "configure email first" step.
/// </summary>
public sealed class SetupService : ISetupService
{
    private readonly IUserAccessRepository _repository;
    private readonly IUserManagementService _userManagement;
    private readonly ILocalAuthService _localAuth;
    private readonly IExternalTokenValidator _externalTokenValidator;
    private readonly INotificationSettingsService _notificationSettings;

    private readonly ILogger<SetupService> _logger;

    public SetupService(
        IUserAccessRepository repository,
        IUserManagementService userManagement,
        ILocalAuthService localAuth,
        IExternalTokenValidator externalTokenValidator,
        INotificationSettingsService notificationSettings,
        ILogger<SetupService>? logger = null)
    {
        _repository = repository;
        _userManagement = userManagement;
        _localAuth = localAuth;
        _externalTokenValidator = externalTokenValidator;
        _notificationSettings = notificationSettings;
        _logger = logger ?? NullLogger<SetupService>.Instance;
    }

    public async Task<bool> RequiresSetupAsync(CancellationToken cancellationToken)
    {
        var users = await _repository.GetUsersAsync(cancellationToken);
        return users.Count == 0;
    }

    public async Task<LocalLoginResponse> CreateFirstSuperAdminAsync(
        CreateFirstSuperAdminRequest request,
        CancellationToken cancellationToken)
    {
        if (!await RequiresSetupAsync(cancellationToken))
        {
            // Once any user exists the deployment is initialized; the first-run path is closed permanently.
            throw new InvalidOperationException("Setup has already been completed.");
        }

        if (!request.AcceptTerms)
        {
            throw new InvalidOperationException("You must accept the Terms and Conditions to continue.");
        }

        // Forced enabled: this deployment ships as a self-hosted package, so email is always on once the
        // first admin exists rather than requiring a separate "configure email" step beforehand.
        await _notificationSettings.UpdateAsync(
            new UpdateNotificationSettingsRequest(
                IsEnabled: true,
                request.EmailSettings.Host,
                request.EmailSettings.Port,
                request.EmailSettings.EnableSsl,
                request.EmailSettings.Username ?? string.Empty,
                request.EmailSettings.FromAddress,
                request.EmailSettings.FromName,
                request.EmailSettings.Password),
            cancellationToken);

        await _userManagement.CreateLocalUserAsync(
            new CreateLocalUserRequest(
                request.Email,
                request.DisplayName,
                request.Password,
                [UnifiedRoles.SuperAdmin],
                RequirePasswordChange: false,
                request.FirstName,
                request.LastName,
                // HIPAA hardening: MFA is compulsory for the first-run SuperAdmin — hardcoded true, not
                // caller-controlled like the general create-user/invite paths.
                RequireMfa: true,
                TenantId: SeededSecurityIds.DefaultTenantId),
            cancellationToken);

        _logger.LogWarning(
            LogEvents.SuperAdminSetupCompleted,
            "First super-admin account provisioned via local setup. This endpoint is reachable only while the " +
            "users table is empty; a repeat means the table was emptied.");

        return await _localAuth.LoginAsync(new LocalLoginRequest(request.Email, request.Password), cancellationToken);
    }

    public async Task<LocalLoginResponse> CreateFirstSuperAdminViaSsoAsync(
        CreateFirstSuperAdminSsoRequest request,
        CancellationToken cancellationToken)
    {
        if (!await RequiresSetupAsync(cancellationToken))
        {
            // Once any user exists the deployment is initialized; the first-run path is closed permanently.
            throw new InvalidOperationException("Setup has already been completed.");
        }

        var identity = await _externalTokenValidator.ValidateAsync(request.Provider, request.Token, cancellationToken);

        // Create the SuperAdmin directly from the external identity: active, no password, SSO-linked.
        var user = new User(identity.Subject, identity.Email, identity.Name, SeededSecurityIds.DefaultTenantId);
        user.LinkExternalIdentity(identity.Subject, request.Provider);
        // HIPAA hardening: MFA is compulsory for the first-run SuperAdmin, same as the local-password path.
        user.SetMustSetupMfa(true);

        var role = await _repository.GetRoleByNameAsync(UnifiedRoles.SuperAdmin, cancellationToken)
            ?? throw new InvalidOperationException("Setup could not complete. Please contact support.");

        await _repository.AddUserAsync(user, cancellationToken);
        await _repository.AddUserRoleAsync(user.Id, role.Id, cancellationToken);

        _logger.LogWarning(
            LogEvents.SuperAdminSetupCompleted,
            "First super-admin account {UserId} provisioned via SSO setup. This endpoint is reachable only while " +
            "the users table is empty; a repeat means the table was emptied.",
            user.Id);

        return await _localAuth.IssueSessionAsync(user, cancellationToken);
    }
}
