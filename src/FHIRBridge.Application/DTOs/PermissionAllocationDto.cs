namespace FHIRBridge.Application.DTOs;

public sealed record PermissionAllocationDto(
    Guid PermissionId,
    string PermissionName,
    string PermissionDescription,
    bool IsEnabled);

public sealed record UpsertUserPermissionAllocationRequest(bool IsEnabled);
