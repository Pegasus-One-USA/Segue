namespace FHIRBridge.Application.DTOs;

/// <summary>Returned when enrollment begins: the shared secret plus an otpauth URI for QR display.</summary>
public sealed record MfaEnrollmentResponse(string Secret, string OtpAuthUri);

/// <summary>A TOTP code supplied to confirm enrollment or to disable MFA.</summary>
public sealed record MfaCodeRequest(string Code);

/// <summary>Returned once, on successful enrollment: the one-time backup codes to store securely.</summary>
public sealed record MfaEnrollmentConfirmedResponse(IReadOnlyList<string> BackupCodes);

/// <summary>Current MFA state for the signed-in user.</summary>
public sealed record MfaStatusResponse(bool Enabled, DateTime? EnrolledOnUtc, int RemainingBackupCodes);
