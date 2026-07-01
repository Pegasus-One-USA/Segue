namespace FHIRBridge.Application.DTOs;

public sealed record AssignTenantUserRequest(
    string ExternalUserId,
    string? Email,
    string? DisplayName,
    string RoleName);
