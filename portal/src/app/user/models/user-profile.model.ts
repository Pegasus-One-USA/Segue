export type ExtendedUserRole =
  | 'system-admin' | 'tenant-admin' | 'developer'
  | 'pipeline-editor' | 'reviewer' | 'auditor' | 'analyst' | 'viewer';

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
    id: 'system-admin',
    label: 'System Admin',
    description: 'Full system access including tenant management and infrastructure',
    color: '#CC2927',
    permissions: ['All permissions granted'],
  },
  {
    id: 'tenant-admin',
    label: 'Tenant Admin',
    description: 'Manage users, roles, and all resources within the organization',
    color: '#7C3AED',
    permissions: ['User management', 'Pipeline management', 'Org settings', 'Audit logs'],
  },
  {
    id: 'developer',
    label: 'Developer',
    description: 'Build and test FHIR pipelines with full API access',
    color: '#2563EB',
    permissions: ['Pipeline creation', 'API access', 'Schema editor', 'Test runs'],
  },
  {
    id: 'pipeline-editor',
    label: 'Pipeline Editor',
    description: 'Create and modify integration pipelines in the workflow builder',
    color: '#00A89D',
    permissions: ['Pipeline creation', 'Node editing', 'Run pipelines', 'View logs'],
  },
  {
    id: 'reviewer',
    label: 'Reviewer',
    description: 'Review and approve pipeline changes before deployment',
    color: '#D97706',
    permissions: ['View pipelines', 'Add comments', 'Approve / reject changes'],
  },
  {
    id: 'auditor',
    label: 'Auditor',
    description: 'Read-only access to all resources for compliance review',
    color: '#6B7280',
    permissions: ['View all resources', 'Export audit logs'],
  },
  {
    id: 'analyst',
    label: 'Analyst',
    description: 'Access dashboards and reporting for data-driven insights',
    color: '#059669',
    permissions: ['View dashboards', 'Export data', 'Run reports'],
  },
  {
    id: 'viewer',
    label: 'Viewer',
    description: 'Read-only access to dashboards and pipeline definitions',
    color: '#94A3B8',
    permissions: ['View dashboards', 'View pipelines'],
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
