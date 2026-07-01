using FHIRBridge.Application.Abstractions.Audit;
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
    private readonly IOperationalAuditService _auditService;

    public UserAccessService(
        IUserAccessRepository repository,
        ICurrentUserService currentUserService,
        IOperationalAuditService auditService)
    {
        _repository = repository;
        _currentUserService = currentUserService;
        _auditService = auditService;
    }

    public async Task<UserProfileDto> GetCurrentUserProfileAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: false, cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    public async Task<UserProfileDto> RecordLoginAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: true, cancellationToken);

        await _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                Guid.Empty,
                null,
                null,
                null,
                null,
                null,
                null,
                "UserLogin",
                "Completed",
                "FHIRBridge portal login observed.",
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    public async Task<IReadOnlyList<TenantUserDto>> GetTenantUsersAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenantUsers = await _repository.GetTenantUsersAsync(tenantId, cancellationToken);

        return await ToTenantUserDtosAsync(tenantUsers, cancellationToken);
    }

    public async Task<TenantUserDto> AssignTenantUserAsync(
        Guid tenantId,
        AssignTenantUserRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalUserId))
        {
            throw new InvalidOperationException("External user id is required.");
        }

        var role = await _repository.GetRoleByNameAsync(request.RoleName, cancellationToken)
            ?? throw new InvalidOperationException($"Role '{request.RoleName}' is not configured.");

        var user = await _repository.GetUserByExternalIdAsync(request.ExternalUserId, cancellationToken);
        if (user is null)
        {
            user = new User(request.ExternalUserId, request.Email, request.DisplayName);
            await _repository.AddUserAsync(user, cancellationToken);
        }
        else
        {
            user.UpdateProfile(request.Email, request.DisplayName);
            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        var tenantUser = await _repository.GetTenantUserAsync(tenantId, user.Id, cancellationToken);
        if (tenantUser is null)
        {
            tenantUser = new TenantUser(tenantId, user.Id, role.Id);
            await _repository.AddTenantUserAsync(tenantUser, cancellationToken);
        }
        else
        {
            tenantUser.UpdateRole(role.Id);
            tenantUser.Activate();
            await _repository.UpdateTenantUserAsync(tenantUser, cancellationToken);
        }

        await _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                tenantId,
                null,
                null,
                null,
                null,
                null,
                null,
                "TenantUserAssigned",
                "Completed",
                $"Tenant user role assigned: {role.Name}.",
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);

        return await ToTenantUserDtoAsync(tenantUser, cancellationToken);
    }

    private async Task<User> GetOrCreateCurrentUserAsync(
        bool recordLogin,
        CancellationToken cancellationToken)
    {
        var currentUser = _currentUserService.CurrentUser;
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
        {
            throw new InvalidOperationException("Authenticated user id claim is missing.");
        }

        var user = await _repository.GetUserByExternalIdAsync(currentUser.ExternalUserId, cancellationToken);
        if (user is null)
        {
            user = new User(currentUser.ExternalUserId, currentUser.Email, currentUser.DisplayName);
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
        var memberships = await _repository.GetTenantMembershipsByUserIdAsync(user.Id, cancellationToken);
        var tenantMemberships = await ToTenantUserDtosAsync(memberships, cancellationToken);

        return new UserProfileDto(
            user.Id,
            user.ExternalUserId,
            user.Email,
            user.DisplayName,
            _currentUserService.CurrentUser.Roles,
            tenantMemberships);
    }

    private async Task<IReadOnlyList<TenantUserDto>> ToTenantUserDtosAsync(
        IReadOnlyCollection<TenantUser> tenantUsers,
        CancellationToken cancellationToken)
    {
        var dtos = new List<TenantUserDto>();
        foreach (var tenantUser in tenantUsers)
        {
            dtos.Add(await ToTenantUserDtoAsync(tenantUser, cancellationToken));
        }

        return dtos;
    }

    private async Task<TenantUserDto> ToTenantUserDtoAsync(
        TenantUser tenantUser,
        CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(tenantUser.UserId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant user references a missing user.");

        var role = await GetRoleNameByIdAsync(tenantUser.RoleId, cancellationToken);

        return new TenantUserDto(
            tenantUser.Id,
            tenantUser.TenantId,
            user.Id,
            user.ExternalUserId,
            user.Email,
            user.DisplayName,
            role,
            tenantUser.IsEnabled);
    }

    private async Task<string> GetRoleNameByIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken);

        return role?.Name ?? "Unknown";
    }
}
