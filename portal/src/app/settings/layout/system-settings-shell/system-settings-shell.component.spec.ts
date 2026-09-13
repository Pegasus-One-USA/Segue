import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { SystemSettingsShellComponent } from './system-settings-shell.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { IRoleService } from '../../../user-management/services/i-role.service';
import { Role, User } from '../../../auth/models/user.model';
import { makeTestUser } from '../../../auth/testing/auth-test-helpers';

/**
 * RBAC Fix 6: the General/Security/SSO Configurations sections (the three `superAdminOnly` entries in
 * SYSTEM_SETTINGS_SECTIONS) are now visible when the caller is the literal SuperAdmin claim (unchanged)
 * OR holds any role with Full System Access — resolved the same way settings-shell.component.ts's
 * callerHasFullAccess (Fix 5) / role-dialog.component.ts / both guards do: the real per-role IsFullAccess
 * flag via IRoleService.getRoles(), cross-referenced by name against the roles this session's own claims
 * say it holds. Never a hardcoded role name for the new capability. Email's own permission-based
 * visibility is completely untouched.
 */
describe('SystemSettingsShellComponent', () => {
  let roleService: jasmine.SpyObj<IRoleService>;
  let authStore: AuthStore;

  function makeRole(name: string, isFullAccess: boolean): Role {
    return {
      id: name, name, displayName: name, description: `${name} role.`, permissions: [],
      color: '#000', isSystemRole: false, isFullAccess, createdAt: '',
    };
  }

  function setup(user: User | null): void {
    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);

    TestBed.configureTestingModule({
      imports: [SystemSettingsShellComponent],
      providers: [
        AuthStore,
        provideRouter([]),
        { provide: IRoleService, useValue: roleService },
      ],
    });

    authStore = TestBed.inject(AuthStore);
    authStore.setUser(user);
  }

  function createAndRender(): SystemSettingsShellComponent {
    const fixture = TestBed.createComponent(SystemSettingsShellComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  function sectionLabels(component: SystemSettingsShellComponent): string[] {
    return component.sections().map(s => s.label);
  }

  const SUPER_ADMIN_ONLY_LABELS = ['General', 'Security', 'SSO Configurations'];

  // ── SuperAdmin → General/Security/SSO sections visible ──────────────────────────────
  it('shows General/Security/SSO Configurations for the existing SuperAdmin, without ever calling getRoles()', () => {
    setup(makeTestUser([], 'SuperAdmin'));

    const labels = sectionLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).toContain(label);
    }
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  // ── Custom Full Access role → sections visible ──────────────────────────────────────
  it('shows General/Security/SSO Configurations for a custom role with IsFullAccess=true', () => {
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Healthcare Platform Admin', [fullAccessRole]));
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const labels = sectionLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).toContain(label);
    }
  });

  // ── Normal custom role → sections remain hidden ─────────────────────────────────────
  it('hides General/Security/SSO Configurations for a normal custom role (IsFullAccess=false)', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(of([normalRole]));

    const labels = sectionLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
  });

  // ── Multiple roles with one Full Access → sections visible ─────────────────────────
  it('shows the sections when at least one of multiple held roles has IsFullAccess=true', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole, fullAccessRole]));
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const labels = sectionLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).toContain(label);
    }
  });

  // ── Multiple normal roles → sections hidden ─────────────────────────────────────────
  it('hides the sections when holding only multiple normal roles', () => {
    const roleA = makeRole('Epic Integration Manager', false);
    const roleB = makeRole('Reporting Viewer', false);
    setup(makeTestUser([], 'Epic Integration Manager', [roleA, roleB]));
    roleService.getRoles.and.returnValue(of([roleA, roleB]));

    const labels = sectionLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
  });

  // ── getRoles() failure → sections remain hidden/fail closed ─────────────────────────
  it('fails closed (hides the sections) when the role lookup errors', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const labels = sectionLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
  });

  // ── Plain Admin → existing behavior unchanged ───────────────────────────────────────
  it('still hides General/Security/SSO Configurations for plain Admin (isAdmin() alone never satisfied superAdminOnly)', () => {
    setup(makeTestUser([], 'Admin'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = sectionLabels(createAndRender());

    // isAdmin() unlocks permission-gated sections (see the next test) but never superAdminOnly ones —
    // unchanged from before this fix.
    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
    expect(labels).toContain('Email');
  });

  // ── Other permission-based settings sections remain unchanged ──────────────────────
  it('leaves Email\'s permission-based visibility completely unaffected', () => {
    setup(makeTestUser(['configuration.view'], 'Epic Integration Manager'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = sectionLabels(createAndRender());

    expect(labels).toContain('Email');
    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
  });

  it('hides Email for a role with none of its permissions, unaffected by this fix', () => {
    setup(makeTestUser([], 'Epic Integration Manager', [makeRole('Epic Integration Manager', false)]));
    roleService.getRoles.and.returnValue(of([makeRole('Epic Integration Manager', false)]));

    const labels = sectionLabels(createAndRender());

    expect(labels).not.toContain('Email');
  });
});
