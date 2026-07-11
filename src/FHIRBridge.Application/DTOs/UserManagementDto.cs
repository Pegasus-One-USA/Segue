using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record UserManagementDto(
    Guid Id,
    string ExternalUserId,
    string? Email,
    string? DisplayName,
    UserStatus Status,
    bool IsEnabled,
    bool IsLocalLoginEnabled,
    bool MustChangePassword,
    IReadOnlyList<string> GlobalRoleNames,
    DateTime CreatedOnUtc,
    DateTime? LastLoginOnUtc,
    bool MfaEnabled,
    bool MustSetupMfa);
