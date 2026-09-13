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
    string? ModifiedBy = null,
    // RBAC redesign Step 4: current value of Role.IsFullAccess — read-only here (this DTO is a response
    // shape; grant/revoke goes through CreateRoleRequest/UpdateRoleRequest, see their own comments).
    bool IsFullAccess = false);
