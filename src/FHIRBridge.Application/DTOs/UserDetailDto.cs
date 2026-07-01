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
    DateTime CreatedOnUtc,
    DateTime? LastLoginOnUtc,
    string? InvitationToken);
