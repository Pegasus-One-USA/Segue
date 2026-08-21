using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public interface ILocalAuthService
{
    Task<LocalLoginResponse> LoginAsync(LocalLoginRequest request, CancellationToken cancellationToken);

    /// <summary>Completes a login that returned <see cref="LocalLoginResponse.RequiresMfa"/>, exchanging the
    /// challenge token plus a TOTP/backup code for a full session.</summary>
    Task<LocalLoginResponse> CompleteMfaLoginAsync(MfaLoginRequest request, CancellationToken cancellationToken);

    Task<LocalLoginResponse> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken);

    Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);

    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);

    /// <summary>Requests a passwordless sign-in link email. Always reports success (no user enumeration),
    /// mirroring <see cref="ForgotPasswordAsync"/>.</summary>
    Task<MagicLinkResponse> RequestMagicLinkAsync(MagicLinkRequest request, CancellationToken cancellationToken);

    /// <summary>Redeems a magic-link token. Same MFA branch as <see cref="LoginAsync"/>: returns an MFA
    /// challenge instead of a full session when the account has MFA enabled.</summary>
    Task<LocalLoginResponse> RedeemMagicLinkAsync(MagicLinkRedeemRequest request, CancellationToken cancellationToken);

    Task<LocalLoginResponse> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Mints a FHIRBridge session (access + refresh token, roles, permissions) for an already-authenticated
    /// user, without a password check. Used by the SSO token-exchange endpoints after an external IdP token
    /// has been validated. Records the login and returns the same <see cref="LocalLoginResponse"/> shape.
    /// </summary>
    Task<LocalLoginResponse> IssueSessionAsync(User user, CancellationToken cancellationToken);

    Task LogoutAsync(CancellationToken cancellationToken);
}
