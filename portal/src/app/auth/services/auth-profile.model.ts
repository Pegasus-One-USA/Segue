/**
 * Mirrors the backend's UserProfileDto. HIPAA #7: the portal builds its User (roles + permissions) from
 * this profile now, not by decoding the access-token JWT client-side — the token lives in an HttpOnly
 * cookie the browser never exposes to JS.
 */
export interface AuthProfileDto {
  userId:         string;
  externalUserId: string;
  email:          string | null;
  displayName:    string | null;
  claimRoles:     string[];
  permissions?:   string[] | null;
  requiresPasswordChange?: boolean;
  requiresMfaSetup?:       boolean;
  // Real, DB-sourced tenant membership (see UserProfileDto/UserAccessService.ToProfileDtoAsync) —
  // replaces the portal's former hardcoded orgId:'org' placeholder.
  tenantId?:   string;
  tenantName?: string;
  // Real account-creation/last-login timestamps (see UserProfileDto.CreatedOnUtc/LastLoginOnUtc) — feed
  // the profile page's Member Since / Last Login fields. lastLoginOnUtc is null for a user who has never
  // logged in before (e.g. mid-invitation-acceptance).
  createdOnUtc?:   string;
  lastLoginOnUtc?: string | null;
}

/** Mirrors the backend LocalLoginResponse with the raw token fields stripped (see AuthController.IssueTokenCookiesAndStrip). */
export interface LocalLoginResponseDto {
  requiresMfa:               boolean;
  mfaChallengeToken?:        string | null;
  mfaChallengeExpiresOnUtc?: string | null;
  tokenType:                 string | null;
  expiresOnUtc:              string | null;
  requiresPasswordChange:    boolean;
  profile?:                  AuthProfileDto | null;
  refreshTokenExpiresOnUtc?: string | null;
  requiresMfaSetup?:         boolean;
}
