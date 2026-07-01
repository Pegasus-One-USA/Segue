namespace FHIRBridge.Application.DTOs;

public sealed record InviteUserRequest(
    string Email,
    Guid RoleId,
    string? FirstName,
    string? LastName);
