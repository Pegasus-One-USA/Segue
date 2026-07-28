/** Matches the API's SystemSettingDto shape exactly, so no DTO↔model mapping is needed. */
export interface SystemSetting {
  id: string;
  key: string;
  value: string;
  description: string | null;
  createdOnUtc: string;
  modifiedOnUtc: string | null;
}

export interface SetSystemSettingRequest {
  value: string;
  description: string | null;
}

/** Matches the API's DecryptProvisionedSecretResponse shape exactly. */
export interface DecryptProvisionedSecretResponse {
  plaintextValue: string;
}
