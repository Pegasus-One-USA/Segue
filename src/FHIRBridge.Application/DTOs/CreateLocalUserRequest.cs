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
    bool RequireMfa = true,
    // Resolved server-side by the caller (UsersController from the creating admin's own tenant via
    // ICurrentTenantResolver; SetupService from the well-known Default Tenant) — never bound from the
    // HTTP request body. Defaults to Guid.Empty only so existing/test call sites that predate tenancy
    // still compile; every real caller now sets this explicitly.
    Guid TenantId = default);
