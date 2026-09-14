namespace FHIRBridge.Application.DTOs;

public sealed record UpdateRoleRequest(
    string Name,
    string Description,
    IReadOnlyCollection<Guid> PermissionIds,
    // RBAC redesign Step 4: requests a change to the role's "Full System Access" flag (see
    // Role.IsFullAccess). Deliberately nullable, NOT a plain bool: null means "this caller isn't touching
    // this field at all" and is always a no-op, regardless of the role's current value or who's calling —
    // this is what lets an older caller that never sends this property (e.g. the current frontend, or
    // SuperAdmin re-saving Admin's permission grants via the Role Permissions screen) keep working exactly
    // as before, even though Admin's IsFullAccess is already true. Only an explicit true/false that
    // actually differs from the role's current value is treated as a real change request, and
    // RoleManagementService.UpdateRoleAsync then requires the ACTING caller to already have Full System
    // Access themselves before honoring it — see that method's own comment for why.
    bool? IsFullAccess = null);
