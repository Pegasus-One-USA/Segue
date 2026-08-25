using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed class UserAccessService : IUserAccessService
{
    private readonly IUserAccessRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserPermissionsProvider _permissionsProvider;
    private readonly ITenantRepository _tenantRepository;

    public UserAccessService(
        IUserAccessRepository repository,
        ICurrentUserService currentUserService,
        IUserPermissionsProvider permissionsProvider,
        ITenantRepository tenantRepository)
    {
        _repository = repository;
        _currentUserService = currentUserService;
        _permissionsProvider = permissionsProvider;
        _tenantRepository = tenantRepository;
    }

    public async Task<UserProfileDto> GetCurrentUserProfileAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: false, cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    public async Task<UserProfileDto> RecordLoginAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: true, cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    private async Task<User> GetOrCreateCurrentUserAsync(
        bool recordLogin,
        CancellationToken cancellationToken)
    {
        var currentUser = _currentUserService.CurrentUser;
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
        {
            throw new InvalidOperationException("Your session is no longer valid. Please sign in again.");
        }

        var user = await _repository.GetUserByExternalIdAsync(currentUser.ExternalUserId, cancellationToken);
        if (user is null)
        {
            // JIT-provisioned on first SSO/Entra login — there is no tenant-selection step in that flow
            // (out of scope per this change; see SetupService's identical reasoning), so this assigns the
            // well-known Default Tenant, same as every pre-existing user migrated into it.
            user = new User(
                currentUser.ExternalUserId, currentUser.Email, currentUser.DisplayName,
                SeededSecurityIds.DefaultTenantId);
            await _repository.AddUserAsync(user, cancellationToken);
        }
        else
        {
            user.UpdateProfile(currentUser.Email, currentUser.DisplayName);
            if (recordLogin)
            {
                user.RecordLogin();
            }

            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        if (recordLogin && user.LastLoginOnUtc is null)
        {
            user.RecordLogin();
            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        return user;
    }

    private async Task<UserProfileDto> ToProfileDtoAsync(
        User user,
        CancellationToken cancellationToken)
    {
        // Looked up fresh (DB-backed, short-lived cache) rather than read off the JWT's claims — the
        // token no longer carries a "permissions" claim at all (see IUserPermissionsProvider).
        var permissions = await _permissionsProvider.GetEffectivePermissionCodesAsync(user.Id, cancellationToken);

        // Real, DB-sourced tenant name for display — replaces the portal's former hardcoded orgId:'org'.
        // Not cached the way permissions/tenant-id-only resolution is (see ICurrentTenantResolver) since
        // this endpoint already does several other DB reads per call; a tenant name changing takes effect
        // on this user's very next /auth/me call either way.
        var tenant = await _tenantRepository.GetByIdAsync(user.TenantId, cancellationToken);

        return new UserProfileDto(
            user.Id,
            user.ExternalUserId,
            user.Email,
            user.DisplayName,
            _currentUserService.CurrentUser.Roles,
            permissions,
            user.RequiresPasswordChange,
            user.IsMfaSetupRequired,
            user.TenantId,
            tenant?.Name ?? string.Empty);
    }
}
