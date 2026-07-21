/** Matches the API's AppSecretDto shape exactly, so no DTO↔model mapping is needed. */
export interface AppSecret {
  secretName: string;
  displayName: string;
  provisioned: boolean;
  lastRotatedUtc: string | null;
  restartRequiredForFullEffect: boolean;
}
