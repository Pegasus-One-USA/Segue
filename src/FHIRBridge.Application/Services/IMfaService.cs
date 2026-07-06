using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// TOTP-based multi-factor enrollment and management for the signed-in user. Login-time verification
/// of the second factor lives in <see cref="ILocalAuthService"/> so it is enforced on every sign-in.
/// </summary>
public interface IMfaService
{
    Task<MfaStatusResponse> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Generates and stages a new TOTP secret; returns the secret + otpauth URI for QR display.</summary>
    Task<MfaEnrollmentResponse> BeginEnrollmentAsync(CancellationToken cancellationToken);

    /// <summary>Confirms enrollment by validating a code against the staged secret; returns backup codes once.</summary>
    Task<MfaEnrollmentConfirmedResponse> ConfirmEnrollmentAsync(MfaCodeRequest request, CancellationToken cancellationToken);

    /// <summary>Disables MFA after validating a current TOTP or backup code.</summary>
    Task DisableAsync(MfaCodeRequest request, CancellationToken cancellationToken);
}
