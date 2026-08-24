namespace FHIRBridge.Application.DTOs;

/// <summary><see cref="RememberMe"/> controls whether the session that results from this login
/// persists across a full browser close (see AuthController.CookieOptionsFor) — it never affects
/// authentication itself, only how long the resulting refresh/CSRF cookies survive.</summary>
public sealed record LocalLoginRequest(string Email, string Password, bool RememberMe = false);

/// <summary>Second step of an MFA-gated login: the challenge token issued by <see cref="LocalLoginRequest"/>,
/// plus the current TOTP code or one of the user's one-time backup codes. <see cref="RememberMe"/> must
/// carry forward the choice made on the original <see cref="LocalLoginRequest"/> — the client is
/// responsible for resending it here since no session/tokens exist yet to store it against.</summary>
public sealed record MfaLoginRequest(string ChallengeToken, string Code, bool RememberMe = false);
