using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

/// <summary>
/// First-run setup. "Requires setup" is simply "no user exists yet". Creating the first SuperAdmin reuses the normal
/// local-user creation + login paths, then signs the caller in. The empty-database guard is re-checked here so the
/// endpoint is self-guarding even though it is anonymous.
/// </summary>
public sealed class SetupService : ISetupService
{
    private readonly IUserAccessRepository _repository;
    private readonly IUserManagementService _userManagement;
    private readonly ILocalAuthService _localAuth;
    private readonly IExternalTokenValidator _externalTokenValidator;

    public SetupService(
        IUserAccessRepository repository,
        IUserManagementService userManagement,
        ILocalAuthService localAuth,
        IExternalTokenValidator externalTokenValidator)
    {
        _repository = repository;
        _userManagement = userManagement;
        _localAuth = localAuth;
        _externalTokenValidator = externalTokenValidator;
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

        await _userManagement.CreateLocalUserAsync(
            new CreateLocalUserRequest(
                request.Email,
                request.DisplayName,
                request.Password,
                [UnifiedRoles.SuperAdmin],
                RequirePasswordChange: false,
                request.FirstName,
                request.LastName),
            cancellationToken);

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
        var user = new User(identity.Subject, identity.Email, identity.Name);
        user.LinkExternalIdentity(identity.Subject, request.Provider);

        var role = await _repository.GetRoleByNameAsync(UnifiedRoles.SuperAdmin, cancellationToken)
            ?? throw new InvalidOperationException("The SuperAdmin role is not configured.");

        await _repository.AddUserAsync(user, cancellationToken);
        await _repository.AddUserRoleAsync(user.Id, role.Id, cancellationToken);

        return await _localAuth.IssueSessionAsync(user, cancellationToken);
    }
}
