namespace FHIRBridge.Application.DTOs;

public sealed record TenantUserDto(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    string ExternalUserId,
    string? Email,
    string? DisplayName,
    string RoleName,
    bool IsEnabled);
