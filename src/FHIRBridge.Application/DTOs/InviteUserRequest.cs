namespace FHIRBridge.Application.DTOs;

public sealed record InviteUserRequest(
    string Email,
    Guid RoleId,
    string? FirstName,
    string? LastName,
    // Resolved server-side by UsersController from the inviting admin's own tenant via
    // ICurrentTenantResolver — never bound from the HTTP request body. See CreateLocalUserRequest.TenantId
    // for the same reasoning.
    Guid TenantId = default);
