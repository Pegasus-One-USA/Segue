namespace FHIRBridge.Application.DTOs;

public sealed record UserProfileDto(
    Guid UserId,
    string ExternalUserId,
    string? Email,
    string? DisplayName,
    IReadOnlyList<string> ClaimRoles);
