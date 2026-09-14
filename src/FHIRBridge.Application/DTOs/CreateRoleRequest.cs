namespace FHIRBridge.Application.DTOs;

public sealed record CreateRoleRequest(
    string Name,
    string Description,
    IReadOnlyCollection<Guid> PermissionIds,
    // RBAC redesign Step 4: requests the new role be created with "Full System Access" (see
    // Role.IsFullAccess). Defaults to false, so an older caller that never sends this property creates a
    // role exactly as before this field existed. RoleManagementService.CreateRoleAsync rejects
    // IsFullAccess = true unless the ACTING caller already has Full System Access themselves — see that
    // method's own comment for why.
    bool IsFullAccess = false);
