import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { SettingsShellComponent } from './settings-shell.component';
import { AuthStore } from '../../auth/store/auth.store';
import { IRoleService } from '../../user-management/services/i-role.service';
import { Role, User } from '../../auth/models/user.model';
import { makeTestUser } from '../../auth/testing/auth-test-helpers';

/**
 * RBAC Fix 5: the "Allowed Origins" tab (the only `superAdminOnly` entry in SETTINGS_TABS) is now visible
 * when the caller is the literal SuperAdmin claim (unchanged) OR holds any role with Full System Access —
 * resolved the same way role-dialog.component.ts's callerHasFullAccess / super-admin.guard.ts /
 * settings-landing.guard.ts do: the real per-role IsFullAccess flag via IRoleService.getRoles(),
 * cross-referenced by name against the roles this session's own claims say it holds. Never a hardcoded
 * role name for the new capability. Every other tab's own visibility rule is completely untouched.
 */
describe('SettingsShellComponent', () => {
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
      imports: [SettingsShellComponent],
      providers: [
        AuthStore,
        provideRouter([]),
        { provide: IRoleService, useValue: roleService },
      ],
    });

    authStore = TestBed.inject(AuthStore);
    authStore.setUser(user);
  }

  function createAndRender(): SettingsShellComponent {
    const fixture = TestBed.createComponent(SettingsShellComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  function tabLabels(component: SettingsShellComponent): string[] {
    return component.tabs().map(t => t.label);
  }

  // ── SuperAdmin → Allowed Origins visible ────────────────────────────────────────────
  it('shows Allowed Origins for the existing SuperAdmin, without ever calling getRoles()', () => {
    setup(makeTestUser([], 'SuperAdmin'));

    const component = createAndRender();

    expect(tabLabels(component)).toContain('Allowed Origins');
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  // ── Custom Full Access role → Allowed Origins visible ───────────────────────────────
  it('shows Allowed Origins for a custom role with IsFullAccess=true', () => {
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Healthcare Platform Admin', [fullAccessRole]));
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const component = createAndRender();

    expect(tabLabels(component)).toContain('Allowed Origins');
  });

  // ── Normal custom role → Allowed Origins remains hidden ─────────────────────────────
  it('hides Allowed Origins for a normal custom role (IsFullAccess=false)', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(of([normalRole]));

    const component = createAndRender();

    expect(tabLabels(component)).not.toContain('Allowed Origins');
  });

  // ── Real-shape divergence: JWT id (=name placeholder) vs API id (real GUID), plus a divergent
  // displayName -- neither may affect the result; matching is by `name` alone (team lead review
  // comments #1/#2). The plain makeRole() helper above can't exercise this since it sets
  // id === name === displayName on both sides.
  it('shows Allowed Origins when matching by name alone, unaffected by a JWT-vs-API id mismatch or a divergent displayName', () => {
    // Mirrors jwt-user.mapper.ts's real shape: the held role's id is the role NAME used as a
    // placeholder, never a real database identifier.
    const heldRole: Role = {
      id: 'Healthcare Platform Admin', name: 'Healthcare Platform Admin', displayName: 'Healthcare Platform Admin',
      description: 'role.', permissions: [], color: '#000', isSystemRole: true, isFullAccess: false, createdAt: '',
    };
    // Mirrors the real API shape (RoleDto): a real database GUID as id, and a displayName that
    // deliberately differs from name.
    const apiRole: Role = {
      id: '3fa85f64-5717-4562-b3fc-2c963f66afa6', name: 'Healthcare Platform Admin', displayName: 'Platform Administrator',
      description: 'role.', permissions: [], color: '#000', isSystemRole: false, isFullAccess: true, createdAt: '',
    };
    setup(makeTestUser([], 'Healthcare Platform Admin', [heldRole]));
    roleService.getRoles.and.returnValue(of([apiRole]));

    const component = createAndRender();

    expect(tabLabels(component)).toContain('Allowed Origins');
  });

  // ── Multiple roles with one Full Access → visible ───────────────────────────────────
  it('shows Allowed Origins when at least one of multiple held roles has IsFullAccess=true', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole, fullAccessRole]));
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const component = createAndRender();

    expect(tabLabels(component)).toContain('Allowed Origins');
  });

  // ── Multiple normal roles → hidden ───────────────────────────────────────────────────
  it('hides Allowed Origins when holding only multiple normal roles', () => {
    const roleA = makeRole('Epic Integration Manager', false);
    const roleB = makeRole('Reporting Viewer', false);
    setup(makeTestUser([], 'Epic Integration Manager', [roleA, roleB]));
    roleService.getRoles.and.returnValue(of([roleA, roleB]));

    const component = createAndRender();

    expect(tabLabels(component)).not.toContain('Allowed Origins');
  });

  // ── Role lookup/API failure → fail closed/hidden ────────────────────────────────────
  it('fails closed (hides Allowed Origins) when the role lookup errors', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const component = createAndRender();

    expect(tabLabels(component)).not.toContain('Allowed Origins');
  });

  // ── Existing settings tabs remain unchanged ──────────────────────────────────────────
  it('leaves every other settings tab governed exactly as before this fix', () => {
    setup(makeTestUser(['configuration.write', 'ehrendpoints.view'], 'Epic Integration Manager'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = tabLabels(createAndRender());

    expect(labels).toContain('Branding');
    expect(labels).toContain('EHR Endpoints');
    expect(labels).not.toContain('Workflow Configurations');
    expect(labels).not.toContain('System Settings');
    expect(labels).not.toContain('Allowed Origins');
  });

  it('still shows every permission-gated tab for isAdmin(), unaffected by this fix', () => {
    setup(makeTestUser([], 'Admin'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = tabLabels(createAndRender());

    expect(labels).toContain('Branding');
    expect(labels).toContain('Workflow Configurations');
    expect(labels).toContain('EHR Endpoints');
    expect(labels).toContain('System Settings');
    // isAdmin() alone does not satisfy superAdminOnly -- unchanged from before this fix.
    expect(labels).not.toContain('Allowed Origins');
  });
});
