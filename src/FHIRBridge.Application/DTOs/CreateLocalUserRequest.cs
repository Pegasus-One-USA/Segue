namespace FHIRBridge.Application.DTOs;

public sealed record CreateLocalUserRequest(
    string Email,
    string? DisplayName,
    string Password,
    IReadOnlyCollection<string> RoleNames,
    bool RequirePasswordChange);
