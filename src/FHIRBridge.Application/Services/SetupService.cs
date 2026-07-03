using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;

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

    public SetupService(
        IUserAccessRepository repository,
        IUserManagementService userManagement,
        ILocalAuthService localAuth)
    {
        _repository = repository;
        _userManagement = userManagement;
        _localAuth = localAuth;
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
                RequirePasswordChange: false),
            cancellationToken);

        return await _localAuth.LoginAsync(new LocalLoginRequest(request.Email, request.Password), cancellationToken);
    }
}
