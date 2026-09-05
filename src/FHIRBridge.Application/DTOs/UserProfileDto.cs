namespace FHIRBridge.Application.DTOs;

public sealed record UserProfileDto(
    Guid UserId,
    string ExternalUserId,
    string? Email,
    string? DisplayName,
    IReadOnlyList<string> ClaimRoles,
    // HIPAA #7: added so the portal can build its permission-gated UI from this profile instead of decoding
    // the (now HttpOnly, unreadable) access-token JWT client-side.
    IReadOnlyList<string>? Permissions = null,
    bool RequiresPasswordChange = false,
    bool RequiresMfaSetup = false,
    // Real, DB-sourced tenant membership — replaces the portal's former hardcoded orgId:'org' placeholder.
    // Always populated: every user has a required TenantId (see User.cs), defaulted to the well-known
    // Default Tenant for pre-existing/JIT-provisioned/first-run users by the AddTenant migration/SetupService.
    Guid TenantId = default,
    string TenantName = "",
    // Real account-creation/last-login timestamps for the portal's own Profile page (Member Since/Last
    // Login) — already tracked on User.cs and already exposed to admins via UserManagementDto/UserDetailDto
    // for OTHER users; this DTO (the current user's own /auth/me profile) just never carried them, which
    // left the portal falling back to "now" for Member Since and nothing at all for Last Login.
    DateTime CreatedOnUtc = default,
    DateTime? LastLoginOnUtc = null);
