import { Injectable, signal, computed } from '@angular/core';
import {
  UserProfile,
  AppTheme,
  ActiveSession,
  ApiKey,
} from '../models/user-profile.model';

const MOCK_PROFILE: UserProfile = {
  id:              'dev-001',
  firstName:       'Hasnain',
  lastName:        'Machiwala',
  email:           'hasnain.machiwala@pegasusone.com',
  phone:           '+1 (415) 555-0182',
  avatarInitials:  'HM',
  avatarColor:     '#00A89D',
  role:            'pipeline-editor',
  roleLabel:       'Pipeline Editor',
  organization:    'Pegasus One Health',
  tenant:          'pegasusone',
  department:      'Healthcare Integration',
  designation:     'Senior Integration Engineer',
  employeeId:      'PO-20241',
  location:        'San Francisco, CA',
  timezone:        'America/Los_Angeles',
  memberSince:     '2024-01-15',
  lastLogin:       '2025-06-25T09:30:00Z',
  status:          'active',
  theme:           'light',
  language:        'English (US)',
  dateFormat:      'MM/DD/YYYY',
  timeFormat:      '12h',
  twoFactorEnabled:     false,
  notificationsEnabled: true,
  emailNotifications:   true,
  inAppNotifications:   true,
  apiKey:          'pk_live_••••••••••••••••••••••••4f3a',
  sessionCount:    2,
};

const MOCK_SESSIONS: ActiveSession[] = [
  {
    id:         's1',
    device:     'MacBook Pro 16"',
    location:   'San Francisco, CA',
    browser:    'Chrome 125',
    lastActive: '2025-06-25T09:30:00Z',
    isCurrent:  true,
  },
  {
    id:         's2',
    device:     'iPhone 15 Pro',
    location:   'San Francisco, CA',
    browser:    'Safari Mobile',
    lastActive: '2025-06-24T18:05:00Z',
    isCurrent:  false,
  },
];

const MOCK_API_KEYS: ApiKey[] = [
  {
    id:         'k1',
    name:       'Production Pipeline Key',
    prefix:     'pk_live_4f3a',
    scopes:     ['pipelines:read', 'pipelines:write', 'logs:read'],
    createdAt:  '2024-03-10',
    lastUsed:   '2025-06-25',
    expiresAt:  null,
  },
  {
    id:         'k2',
    name:       'Dev / Testing Key',
    prefix:     'pk_test_9b2c',
    scopes:     ['pipelines:read', 'pipelines:write'],
    createdAt:  '2024-08-01',
    lastUsed:   '2025-06-20',
    expiresAt:  '2026-08-01',
  },
];

@Injectable({ providedIn: 'root' })
export class UserProfileService {
  private readonly _profile  = signal<UserProfile>({ ...MOCK_PROFILE });
  private readonly _sessions = signal<ActiveSession[]>(MOCK_SESSIONS);
  private readonly _apiKeys  = signal<ApiKey[]>(MOCK_API_KEYS);

  readonly profile  = this._profile.asReadonly();
  readonly sessions = this._sessions.asReadonly();
  readonly apiKeys  = this._apiKeys.asReadonly();

  readonly theme       = computed(() => this._profile().theme);
  readonly fullName    = computed(() =>
    `${this._profile().firstName} ${this._profile().lastName}`
  );
  readonly initials    = computed(() => this._profile().avatarInitials);
  readonly roleLabel   = computed(() => this._profile().roleLabel);

  setTheme(theme: AppTheme): void {
    this._profile.update(p => ({ ...p, theme }));
    document.documentElement.setAttribute('data-theme', theme);
  }

  updateProfile(partial: Partial<UserProfile>): void {
    this._profile.update(p => ({ ...p, ...partial }));
  }

  revokeSession(id: string): void {
    this._sessions.update(sess => sess.filter(s => s.id !== id));
  }

  revokeApiKey(id: string): void {
    this._apiKeys.update(keys => keys.filter(k => k.id !== id));
  }

  toggleTwoFactor(): void {
    this._profile.update(p => ({ ...p, twoFactorEnabled: !p.twoFactorEnabled }));
  }

  updateNotifications(
    email: boolean,
    inApp: boolean,
  ): void {
    this._profile.update(p => ({
      ...p,
      emailNotifications: email,
      inAppNotifications: inApp,
      notificationsEnabled: email || inApp,
    }));
  }
}
