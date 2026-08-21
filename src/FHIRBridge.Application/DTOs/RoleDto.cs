namespace FHIRBridge.Application.DTOs;

public sealed record RoleDto(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<PermissionDto> Permissions,
    bool IsSystemRole,
    DateTime? CreatedOnUtc = null,
    string? CreatedBy = null,
    DateTime? ModifiedOnUtc = null,
    string? ModifiedBy = null);
