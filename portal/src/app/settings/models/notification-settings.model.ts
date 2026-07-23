/** Matches the API's NotificationSettingsDto shape exactly (see NotificationSettingsDto.cs). Never carries the
 *  password — only whether one is currently configured. */
export interface NotificationSettingsModel {
  isEnabled: boolean;
  host: string;
  port: number;
  enableSsl: boolean;
  username: string;
  fromAddress: string;
  fromName: string;
  hasPasswordConfigured: boolean;
}

/** Matches UpdateNotificationSettingsRequest's expected body shape. `password` is write-only: omit/null to keep
 *  whatever password is already saved. */
export interface UpdateNotificationSettingsRequest {
  isEnabled: boolean;
  host: string;
  port: number;
  enableSsl: boolean;
  username: string;
  fromAddress: string;
  fromName: string;
  password: string | null;
}
