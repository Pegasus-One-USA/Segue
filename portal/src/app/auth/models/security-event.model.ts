export type SecurityEventType =
  | 'login_success'
  | 'login_failed'
  | 'account_locked'
  | 'account_unlocked'
  | 'password_changed'
  | 'password_reset'
  | 'invitation_sent'
  | 'invitation_accepted';

export interface SecurityEvent {
  id:        string;
  type:      SecurityEventType;
  userId?:   string;
  email:     string;
  timestamp: Date;
  ip?:       string;
  details?:  string;
}
