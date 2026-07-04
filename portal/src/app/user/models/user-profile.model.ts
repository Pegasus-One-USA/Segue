import { UserRole } from '../../auth/models/user.model';

// Same value space as the auth model's UserRole (the backend's real role names) — kept as a
// distinct alias since this file's RoleDefinition/ROLE_DEFINITIONS predate the auth model and
// are consumed independently by the profile page.
export type ExtendedUserRole = UserRole;

export type AppTheme    = 'light' | 'dark' | 'system';
export type UserStatus  = 'active' | 'inactive' | 'suspended';
export type TimeFormat  = '12h' | '24h';

export interface UserProfile {
  id: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  avatarInitials: string;
  avatarColor: string;
  role: ExtendedUserRole;
  roleLabel: string;
  organization: string;
  tenant: string;
  department: string;
  designation: string;
  employeeId: string;
  location: string;
  timezone: string;
  memberSince: string;
  lastLogin: string;
  status: UserStatus;
  theme: AppTheme;
  language: string;
  dateFormat: string;
  timeFormat: TimeFormat;
  twoFactorEnabled: boolean;
  notificationsEnabled: boolean;
  emailNotifications: boolean;
  inAppNotifications: boolean;
  apiKey: string;
  sessionCount: number;
}

export interface RoleDefinition {
  id: ExtendedUserRole;
  label: string;
  description: string;
  color: string;
  permissions: string[];
}

export const ROLE_DEFINITIONS: RoleDefinition[] = [
  {
    id: 'SuperAdmin',
    label: 'Super Admin',
    description: 'Full platform administrator across all modules and settings',
    color: '#CC2927',
    permissions: ['All permissions granted'],
  },
  {
    id: 'Admin',
    label: 'Admin',
    description: 'Administers configuration and users',
    color: '#7C3AED',
    permissions: ['User management', 'Role management', 'Configuration', 'Audit logs'],
  },
  {
    id: 'Operations',
    label: 'Operations',
    description: 'Builds and runs pipeline configurations, and reviews data and audit output',
    color: '#00A89D',
    permissions: ['Pipeline creation', 'Run pipelines', 'View logs', 'View reports'],
  },
  {
    id: 'Audit',
    label: 'Audit',
    description: 'Read-only access to configuration and audit logs',
    color: '#6B7280',
    permissions: ['View all resources', 'Export audit logs'],
  },
];

export interface ActiveSession {
  id: string;
  device: string;
  location: string;
  browser: string;
  lastActive: string;
  isCurrent: boolean;
}

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  scopes: string[];
  createdAt: string;
  lastUsed: string;
  expiresAt: string | null;
}
