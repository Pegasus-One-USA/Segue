namespace FHIRBridge.Application.DTOs;

public sealed record UpdateLocalUserRequest(
    string? Email,
    string? DisplayName,
    bool IsEnabled,
    IReadOnlyCollection<string> RoleNames,
    string? NewPassword,
    bool RequirePasswordChange);
