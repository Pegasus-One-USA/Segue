namespace FHIRBridge.Application.DTOs;

public sealed record UpdateRoleRequest(
    string Name,
    string Description,
    IReadOnlyCollection<Guid> PermissionIds);
