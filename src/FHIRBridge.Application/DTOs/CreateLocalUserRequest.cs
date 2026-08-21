namespace FHIRBridge.Application.DTOs;

public sealed record CreateLocalUserRequest(
    string Email,
    string? DisplayName,
    string Password,
    IReadOnlyCollection<string> RoleNames,
    bool RequirePasswordChange,
    string? FirstName = null,
    string? LastName = null,
    // HIPAA hardening: defaults true, same as InviteUserRequest — an admin has to deliberately opt a
    // directly-created user OUT of MFA. The first-run SuperAdmin path (SetupService) hardcodes this to
    // true regardless of caller input — MFA is compulsory there, not just default-on.
    bool RequireMfa = true);
