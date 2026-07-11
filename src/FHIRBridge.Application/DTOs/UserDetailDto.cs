using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record UserDetailDto(
    Guid Id,
    string? Email,
    string? FirstName,
    string? LastName,
    string? DisplayName,
    UserStatus Status,
    bool IsEnabled,
    IReadOnlyList<RoleDto> Roles,
    IReadOnlyList<PermissionAllocationDto> DirectPermissionAllocations,
    DateTime CreatedOnUtc,
    DateTime? LastLoginOnUtc,
    string? InvitationToken,
    bool MfaEnabled,
    bool MustSetupMfa);
