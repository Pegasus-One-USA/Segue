namespace FHIRBridge.Application.DTOs;

public sealed record PermissionDto(
    Guid Id,
    string Name,
    string DisplayName,
    string Description,
    Guid? GroupId,
    bool IsVisible);
