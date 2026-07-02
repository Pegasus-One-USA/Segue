import { Injectable, signal, computed, inject, effect } from '@angular/core';
import {
  UserProfile,
  AppTheme,
  ActiveSession,
  ApiKey,
  ROLE_DEFINITIONS,
  ExtendedUserRole,
} from '../models/user-profile.model';
import { AuthService } from '../../auth/services/auth.service';

// ─── Helpers ──────────────────────────────────────────────────────────────────

const AVATAR_COLORS = [
  '#00A89D', '#5B21B6', '#1D4ED8', '#0369A1',
  '#0891B2', '#059669', '#D97706', '#DC2626',
];

function deriveAvatarColor(email: string): string {
  let hash = 0;
  for (const ch of email) hash = (hash * 31 + ch.charCodeAt(0)) & 0xffffffff;
  return AVATAR_COLORS[Math.abs(hash) % AVATAR_COLORS.length];
}

function deriveInitials(first: string, last: string): string {
  return ((first[0] ?? '') + (last[0] ?? '')).toUpperCase() || '?';
}

// ─── Default profile (shown while no user is loaded) ──────────────────────────

const DEFAULT_PROFILE: UserProfile = {
  id:              '',
  firstName:       '',
  lastName:        '',
  email:           '',
  phone:           '',
  avatarInitials:  '?',
  avatarColor:     '#94A3B8',
  role:            'viewer',
  roleLabel:       'Viewer',
  organization:    'FHIRBridge Platform',
  tenant:          '',
  department:      '',
  designation:     '',
  employeeId:      '',
  location:        '',
  timezone:        'UTC',
  memberSince:     '',
  lastLogin:       '',
  status:          'active',
  theme:           'light',
  language:        'English (US)',
  dateFormat:      'MM/DD/YYYY',
  timeFormat:      '12h',
  twoFactorEnabled:     false,
  notificationsEnabled: true,
  emailNotifications:   true,
  inAppNotifications:   true,
  apiKey:          '',
  sessionCount:    0,
};

// ─── Mock sessions & API keys (for settings pages) ────────────────────────────

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

// ─── Service ──────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class UserProfileService {
  private readonly auth = inject(AuthService);

  private readonly _profile  = signal<UserProfile>({ ...DEFAULT_PROFILE });
  private readonly _sessions = signal<ActiveSession[]>(MOCK_SESSIONS);
  private readonly _apiKeys  = signal<ApiKey[]>(MOCK_API_KEYS);

  readonly profile  = this._profile.asReadonly();
  readonly sessions = this._sessions.asReadonly();
  readonly apiKeys  = this._apiKeys.asReadonly();

  readonly theme     = computed(() => this._profile().theme);
  readonly fullName  = computed(() =>
    [this._profile().firstName, this._profile().lastName].filter(Boolean).join(' ')
  );
  readonly initials  = computed(() => this._profile().avatarInitials);
  readonly roleLabel = computed(() => this._profile().roleLabel);

  constructor() {
    // Sync identity fields from the auth store whenever the logged-in user changes.
    // Preference fields (theme, language, dateFormat, etc.) are preserved across syncs.
    effect(() => {
      const user = this.auth.currentUser();
      if (!user) return;

      const roleDef = ROLE_DEFINITIONS.find(r => r.id === user.role);

      this._profile.update(p => ({
        ...p,
        id:              user.id,
        firstName:       user.firstName,
        lastName:        user.lastName,
        email:           user.email,
        phone:           user.phone           ?? p.phone,
        avatarInitials:  deriveInitials(user.firstName, user.lastName),
        avatarColor:     deriveAvatarColor(user.email),
        role:            user.role            as ExtendedUserRole,
        roleLabel:       roleDef?.label       ?? user.role,
        department:      user.department      ?? p.department,
        designation:     user.jobTitle        ?? p.designation,
        memberSince:     user.createdAt,
        lastLogin:       user.lastLoginAt     ?? p.lastLogin,
        status:          user.status          as 'active' | 'inactive' | 'suspended',
        twoFactorEnabled: user.twoFactorEnabled,
        tenant:          user.orgId           ?? p.tenant,
        sessionCount:    this._sessions().length,
      }));
    });
  }

  // ─── Preference & settings mutations ────────────────────────────────────────

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

  updateNotifications(email: boolean, inApp: boolean): void {
    this._profile.update(p => ({
      ...p,
      emailNotifications:   email,
      inAppNotifications:   inApp,
      notificationsEnabled: email || inApp,
    }));
  }
}
