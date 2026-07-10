namespace FHIRBridge.Application.DTOs;

public sealed record LocalLoginRequest(string Email, string Password);

/// <summary>Second step of an MFA-gated login: the challenge token issued by <see cref="LocalLoginRequest"/>,
/// plus the current TOTP code or one of the user's one-time backup codes.</summary>
public sealed record MfaLoginRequest(string ChallengeToken, string Code);
