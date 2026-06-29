import { Component, inject, signal, computed } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { SidebarComponent } from '../../dashboard/layout/sidebar/sidebar.component';
import { UserMenuComponent } from '../../user/components/user-menu/user-menu.component';
import { ExtendedUserRole, UserStatus, ROLE_DEFINITIONS } from '../../user/models/user-profile.model';

export interface OrgUser {
  id: string;
  firstName: string;
  lastName: string;
  email: string;
  avatarInitials: string;
  avatarColor: string;
  role: ExtendedUserRole;
  roleLabel: string;
  department: string;
  status: UserStatus;
  lastLogin: string;
  twoFactorEnabled: boolean;
  isCurrent?: boolean;
}

const MOCK_USERS: OrgUser[] = [
  {
    id: 'u1', firstName: 'Hasnain',  lastName: 'Machiwala', email: 'hasnain.machiwala@pegasusone.com',
    avatarInitials: 'HM', avatarColor: '#00A89D', role: 'pipeline-editor', roleLabel: 'Pipeline Editor',
    department: 'Healthcare Integration', status: 'active', lastLogin: '2025-06-29T09:30:00Z',
    twoFactorEnabled: false, isCurrent: true,
  },
  {
    id: 'u2', firstName: 'Sarah',    lastName: 'Johnson',   email: 'sarah.johnson@pegasusone.com',
    avatarInitials: 'SJ', avatarColor: '#CC2927', role: 'system-admin', roleLabel: 'System Admin',
    department: 'Platform Engineering', status: 'active', lastLogin: '2025-06-29T08:15:00Z',
    twoFactorEnabled: true,
  },
  {
    id: 'u3', firstName: 'Michael',  lastName: 'Chen',      email: 'michael.chen@pegasusone.com',
    avatarInitials: 'MC', avatarColor: '#2563EB', role: 'developer', roleLabel: 'Developer',
    department: 'Healthcare Integration', status: 'active', lastLogin: '2025-06-28T14:22:00Z',
    twoFactorEnabled: true,
  },
  {
    id: 'u4', firstName: 'Emily',    lastName: 'Rodriguez', email: 'emily.rodriguez@pegasusone.com',
    avatarInitials: 'ER', avatarColor: '#7C3AED', role: 'tenant-admin', roleLabel: 'Tenant Admin',
    department: 'Operations', status: 'active', lastLogin: '2025-06-29T07:50:00Z',
    twoFactorEnabled: true,
  },
  {
    id: 'u5', firstName: 'James',    lastName: 'Wilson',    email: 'james.wilson@pegasusone.com',
    avatarInitials: 'JW', avatarColor: '#D97706', role: 'reviewer', roleLabel: 'Reviewer',
    department: 'Quality Assurance', status: 'active', lastLogin: '2025-06-27T16:45:00Z',
    twoFactorEnabled: false,
  },
  {
    id: 'u6', firstName: 'Priya',    lastName: 'Patel',     email: 'priya.patel@pegasusone.com',
    avatarInitials: 'PP', avatarColor: '#6B7280', role: 'auditor', roleLabel: 'Auditor',
    department: 'Compliance', status: 'active', lastLogin: '2025-06-26T11:30:00Z',
    twoFactorEnabled: true,
  },
  {
    id: 'u7', firstName: 'David',    lastName: 'Kim',       email: 'david.kim@pegasusone.com',
    avatarInitials: 'DK', avatarColor: '#059669', role: 'analyst', roleLabel: 'Analyst',
    department: 'Business Intelligence', status: 'inactive', lastLogin: '2025-05-14T09:00:00Z',
    twoFactorEnabled: false,
  },
  {
    id: 'u8', firstName: 'Amanda',   lastName: 'Torres',    email: 'amanda.torres@pegasusone.com',
    avatarInitials: 'AT', avatarColor: '#94A3B8', role: 'viewer', roleLabel: 'Viewer',
    department: 'Business Intelligence', status: 'suspended', lastLogin: '2025-04-02T13:15:00Z',
    twoFactorEnabled: false,
  },
  {
    id: 'u9', firstName: 'Robert',   lastName: 'Nakamura',  email: 'robert.nakamura@pegasusone.com',
    avatarInitials: 'RN', avatarColor: '#2563EB', role: 'developer', roleLabel: 'Developer',
    department: 'Healthcare Integration', status: 'active', lastLogin: '2025-06-28T10:55:00Z',
    twoFactorEnabled: true,
  },
  {
    id: 'u10', firstName: 'Lisa',    lastName: 'Fernandez', email: 'lisa.fernandez@pegasusone.com',
    avatarInitials: 'LF', avatarColor: '#00A89D', role: 'pipeline-editor', roleLabel: 'Pipeline Editor',
    department: 'FHIR Integration', status: 'active', lastLogin: '2025-06-29T06:40:00Z',
    twoFactorEnabled: false,
  },
];

@Component({
  selector: 'app-user-management',
  standalone: true,
  imports: [DatePipe, RouterLink, FormsModule, SidebarComponent, UserMenuComponent],
  templateUrl: './user-management.component.html',
  styleUrl: './user-management.component.scss',
})
export class UserManagementComponent {
  protected readonly sidebarCollapsed = signal(false);

  // ── filter state ───────────────────────────────────────────────────────────
  protected readonly searchQuery  = signal('');
  protected readonly roleFilter   = signal<ExtendedUserRole | ''>('');
  protected readonly statusFilter = signal<UserStatus | ''>('');

  // ── users (mutable for demo actions) ──────────────────────────────────────
  protected readonly users = signal<OrgUser[]>(MOCK_USERS);

  // ── role editing state ─────────────────────────────────────────────────────
  protected readonly editingUserId  = signal<string | null>(null);
  protected readonly editingRole    = signal<ExtendedUserRole | ''>('');

  // ── invite panel ──────────────────────────────────────────────────────────
  protected readonly inviteOpen     = signal(false);
  protected readonly inviteEmail    = signal('');
  protected readonly inviteRole     = signal<ExtendedUserRole>('viewer');
  protected readonly inviteSent     = signal(false);

  // ── computed ───────────────────────────────────────────────────────────────
  readonly filteredUsers = computed(() => {
    const q    = this.searchQuery().toLowerCase().trim();
    const role = this.roleFilter();
    const stat = this.statusFilter();

    return this.users().filter(u => {
      const matchQ    = !q || `${u.firstName} ${u.lastName} ${u.email} ${u.department}`.toLowerCase().includes(q);
      const matchRole = !role || u.role === role;
      const matchStat = !stat || u.status === stat;
      return matchQ && matchRole && matchStat;
    });
  });

  readonly stats = computed(() => {
    const all = this.users();
    return {
      total:     all.length,
      active:    all.filter(u => u.status === 'active').length,
      inactive:  all.filter(u => u.status === 'inactive').length,
      suspended: all.filter(u => u.status === 'suspended').length,
    };
  });

  readonly roles = ROLE_DEFINITIONS;

  // ── actions ────────────────────────────────────────────────────────────────
  toggleSidebar(): void { this.sidebarCollapsed.update(v => !v); }

  toggleStatus(user: OrgUser): void {
    if (user.isCurrent) return;
    const next: UserStatus = user.status === 'active' ? 'suspended' : 'active';
    this.users.update(list => list.map(u => u.id === user.id ? { ...u, status: next } : u));
  }

  startEditRole(user: OrgUser): void {
    if (user.isCurrent) return;
    this.editingUserId.set(user.id);
    this.editingRole.set(user.role);
  }

  saveRole(userId: string): void {
    const newRole = this.editingRole();
    if (!newRole) return;
    const roleDef = ROLE_DEFINITIONS.find(r => r.id === newRole);
    this.users.update(list => list.map(u =>
      u.id === userId ? { ...u, role: newRole as ExtendedUserRole, roleLabel: roleDef?.label ?? u.roleLabel } : u
    ));
    this.editingUserId.set(null);
  }

  cancelEdit(): void { this.editingUserId.set(null); }

  sendInvite(): void {
    if (!this.inviteEmail().trim()) return;
    this.inviteSent.set(true);
    setTimeout(() => {
      this.inviteOpen.set(false);
      this.inviteSent.set(false);
      this.inviteEmail.set('');
      this.inviteRole.set('viewer');
    }, 1800);
  }

  // ── helpers ────────────────────────────────────────────────────────────────
  roleColor(role: ExtendedUserRole): string {
    return ROLE_DEFINITIONS.find(r => r.id === role)?.color ?? '#94A3B8';
  }

  statusLabel(s: UserStatus): string {
    return s === 'active' ? 'Active' : s === 'inactive' ? 'Inactive' : 'Suspended';
  }

  clearFilters(): void {
    this.searchQuery.set('');
    this.roleFilter.set('');
    this.statusFilter.set('');
  }

  get hasFilters(): boolean {
    return !!(this.searchQuery() || this.roleFilter() || this.statusFilter());
  }
}
