namespace FHIRBridge.Application.DTOs;

public sealed record TenantDto(
    Guid Id,
    string Name,
    string Code,
    bool IsActive,
    DateTime CreatedOnUtc,
    string? CreatedBy);
