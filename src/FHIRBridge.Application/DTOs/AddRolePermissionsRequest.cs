namespace FHIRBridge.Application.DTOs;

public sealed record AddRolePermissionsRequest(IReadOnlyCollection<Guid> PermissionIds);
