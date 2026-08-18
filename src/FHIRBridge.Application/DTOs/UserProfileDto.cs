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
    bool RequiresMfaSetup = false);
