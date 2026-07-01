using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IUserAccessService
{
    Task<UserProfileDto> GetCurrentUserProfileAsync(CancellationToken cancellationToken);

    Task<UserProfileDto> RecordLoginAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantUserDto>> GetTenantUsersAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<TenantUserDto> AssignTenantUserAsync(
        Guid tenantId,
        AssignTenantUserRequest request,
        CancellationToken cancellationToken);
}
