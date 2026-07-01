namespace FHIRBridge.Application.DTOs;

public sealed record RoleDto(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<PermissionDto> Permissions,
    bool IsSystemRole);
