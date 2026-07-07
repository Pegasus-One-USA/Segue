namespace FHIRBridge.Application.DTOs;

public sealed record PermissionAllocationDto(
    Guid PermissionId,
    string PermissionName,
    string PermissionDescription,
    bool IsEnabled);

public sealed record UpsertUserPermissionAllocationRequest(bool IsEnabled);

/// <summary>
/// Replaces a user's entire set of direct permission overrides in one call. An empty
/// <paramref name="PermissionIdToIsEnabled"/> clears every override, so the user fully inherits
/// their role-derived permissions again.
/// </summary>
public sealed record SetUserPermissionAllocationsRequest(Dictionary<Guid, bool> PermissionIdToIsEnabled);
