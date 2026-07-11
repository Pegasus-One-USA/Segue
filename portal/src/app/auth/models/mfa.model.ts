/** Mirrors the backend MfaDtos.cs shapes exposed under /api/v1/auth/mfa. */

export interface MfaStatusResponse {
  enabled: boolean;
  enrolledOnUtc: string | null;
  remainingBackupCodes: number;
}

/** Returned when enrollment begins: the shared secret plus an otpauth URI for QR display. */
export interface MfaEnrollmentResponse {
  secret: string;
  otpAuthUri: string;
}

/** A TOTP code (or backup code) supplied to confirm enrollment or to disable MFA. */
export interface MfaCodeRequest {
  code: string;
}

/** Returned once, on successful enrollment: the one-time backup codes to store securely. */
export interface MfaEnrollmentConfirmedResponse {
  backupCodes: string[];
}
