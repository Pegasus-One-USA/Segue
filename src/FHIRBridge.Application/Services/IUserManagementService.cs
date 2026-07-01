using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IUserManagementService
{
    Task<IReadOnlyList<UserManagementDto>> GetUsersAsync(CancellationToken cancellationToken);

    Task<UserManagementDto> CreateLocalUserAsync(CreateLocalUserRequest request, CancellationToken cancellationToken);

    Task<UserManagementDto> UpdateLocalUserAsync(Guid userId, UpdateLocalUserRequest request, CancellationToken cancellationToken);
}
