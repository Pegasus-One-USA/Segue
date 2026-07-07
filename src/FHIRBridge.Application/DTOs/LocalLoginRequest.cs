namespace FHIRBridge.Application.DTOs;

public sealed record LocalLoginRequest(
    string Email,
    string Password,
    // Second factor. Required only when the account has MFA enabled; supply either the current
    // TOTP code or one of the user's one-time backup codes.
    string? MfaCode = null);
