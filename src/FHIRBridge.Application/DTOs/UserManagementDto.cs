namespace FHIRBridge.Application.DTOs;

public sealed record UserManagementDto(
    Guid Id,
    string ExternalUserId,
    string? Email,
    string? DisplayName,
    bool IsEnabled,
    bool IsLocalLoginEnabled,
    bool MustChangePassword,
    IReadOnlyList<string> GlobalRoleNames,
    DateTime CreatedOnUtc,
    DateTime? LastLoginOnUtc);
