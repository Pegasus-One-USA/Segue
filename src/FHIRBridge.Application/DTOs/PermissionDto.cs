namespace FHIRBridge.Application.DTOs;

public sealed record PermissionDto(
    Guid Id,
    string Name,
    string Description,
    Guid? CategoryId);
