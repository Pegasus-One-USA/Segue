/**
 * Minimal ambient typings for the Google Identity Services (GIS) client
 * (loaded dynamically from https://accounts.google.com/gsi/client).
 * Only the surface SsoService uses is declared — enough to keep the build green
 * without pulling in an npm package.
 */
interface GoogleCredentialResponse {
  /** The JWT ID token (credential) issued by Google. */
  credential: string;
  select_by?: string;
  clientId?: string;
}

interface GoogleIdInitializeConfig {
  client_id: string;
  callback: (response: GoogleCredentialResponse) => void;
  auto_select?: boolean;
  cancel_on_tap_outside?: boolean;
  use_fedcm_for_prompt?: boolean;
}

interface GooglePromptNotification {
  isNotDisplayed(): boolean;
  isSkippedMoment(): boolean;
  isDismissedMoment(): boolean;
  getNotDisplayedReason(): string;
  getSkippedReason(): string;
  getDismissedReason(): string;
  getMomentType(): string;
}

interface GoogleAccountsId {
  initialize(config: GoogleIdInitializeConfig): void;
  prompt(momentListener?: (notification: GooglePromptNotification) => void): void;
  cancel(): void;
  disableAutoSelect(): void;
}

interface GoogleAccounts {
  id: GoogleAccountsId;
}

interface Window {
  google?: { accounts: GoogleAccounts };
}

declare const google: { accounts: GoogleAccounts };
