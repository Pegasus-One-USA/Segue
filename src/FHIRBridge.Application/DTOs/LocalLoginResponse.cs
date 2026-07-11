namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Result of a local login attempt. When <see cref="RequiresMfa"/> is true, the password was correct
/// but a second factor is still needed — no tokens are issued yet, and the client must submit
/// <see cref="MfaChallengeToken"/> plus a code to <c>POST /auth/internal/login/mfa</c> to finish. When
/// false, <see cref="AccessToken"/>/<see cref="Profile"/> carry the completed login.
/// </summary>
public sealed record LocalLoginResponse(
    bool RequiresMfa,
    string? MfaChallengeToken,
    DateTime? MfaChallengeExpiresOnUtc,
    string? AccessToken,
    string? TokenType,
    DateTime? ExpiresOnUtc,
    bool RequiresPasswordChange,
    UserProfileDto? Profile,
    string? RefreshToken = null,
    DateTime? RefreshTokenExpiresOnUtc = null,
    // True when an admin has required this account to have MFA enabled but it isn't enrolled yet —
    // the session is issued, but the client should be routed straight to MFA enrollment; the
    // server-side gate middleware blocks everything else until enrolled.
    bool RequiresMfaSetup = false)
{
    public static LocalLoginResponse MfaRequired(string mfaChallengeToken, DateTime mfaChallengeExpiresOnUtc) =>
        new(RequiresMfa: true,
            MfaChallengeToken: mfaChallengeToken,
            MfaChallengeExpiresOnUtc: mfaChallengeExpiresOnUtc,
            AccessToken: null,
            TokenType: null,
            ExpiresOnUtc: null,
            RequiresPasswordChange: false,
            Profile: null);
}
