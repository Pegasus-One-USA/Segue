import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { SettingsShellComponent } from './settings-shell.component';
import { AuthStore } from '../../auth/store/auth.store';
import { IRoleService } from '../../user-management/services/i-role.service';
import { Role, User } from '../../auth/models/user.model';
import { makeTestUser } from '../../auth/testing/auth-test-helpers';

/**
 * RBAC Fix 5: the shell resolves whether the caller holds Full System Access — the literal SuperAdmin claim
 * (no API call) OR any held role with IsFullAccess, resolved via IRoleService.getRoles() and cross-referenced
 * by role NAME (never displayName, never id) against the roles this session's own claims say it holds. It
 * feeds the `superAdminOnly` tab rule.
 *
 * "Allowed Origins" used to be the only `superAdminOnly` tab. It is now a launcher row on System Settings >
 * General (fbd5dcf9, "consolidate settings"), as are EHR Endpoints and License, so none of the three is a tab
 * any more. The row-level SuperAdmin / Full Access visibility is covered in
 * system-setting-list.component.spec.ts; this spec keeps guarding the shell's own Full Access resolution and
 * the tabs that remain.
 */
describe('SettingsShellComponent', () => {
  let roleService: jasmine.SpyObj<IRoleService>;
  let authStore: AuthStore;

  const MOVED_TO_GENERAL_ROWS = ['Allowed Origins', 'EHR Endpoints', 'License'];

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

  /** The shell's resolved Full Access state (private signal) — what the `superAdminOnly` tab rule reads. */
  function resolvedFullAccess(component: SettingsShellComponent): boolean {
    return component['callerHasFullAccess']();
  }

  function expectNoMovedTabs(labels: string[]): void {
    for (const label of MOVED_TO_GENERAL_ROWS) {
      expect(labels).not.toContain(label);
    }
  }

  // ── SuperAdmin → every tab, no role lookup ──────────────────────────────────────────
  it('shows every settings tab for the existing SuperAdmin, without ever calling getRoles()', () => {
    setup(makeTestUser([], 'SuperAdmin'));

    const labels = tabLabels(createAndRender());

    expect(labels).toEqual(['Branding', 'Workflow Configurations', 'System Settings']);
    // Allowed Origins is a System Settings > General row now, not a tab.
    expectNoMovedTabs(labels);
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  // ── Custom Full Access role → resolved as Full Access ───────────────────────────────
  it('resolves Full Access for a custom role with IsFullAccess=true', () => {
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Healthcare Platform Admin', [fullAccessRole]));
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const component = createAndRender();

    expect(resolvedFullAccess(component)).toBeTrue();
    expectNoMovedTabs(tabLabels(component));
  });

  // ── Normal custom role → not Full Access ────────────────────────────────────────────
  it('does not resolve Full Access for a normal custom role (IsFullAccess=false)', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(of([normalRole]));

    const component = createAndRender();

    expect(resolvedFullAccess(component)).toBeFalse();
    expectNoMovedTabs(tabLabels(component));
  });

  // ── Real-shape divergence: JWT id (=name placeholder) vs API id (real GUID), plus a divergent
  // displayName -- neither may affect the result; matching is by `name` alone (team lead review
  // comments #1/#2). The plain makeRole() helper above can't exercise this since it sets
  // id === name === displayName on both sides.
  it('resolves Full Access by name alone, unaffected by a JWT-vs-API id mismatch or a divergent displayName', () => {
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

    expect(resolvedFullAccess(component)).toBeTrue();
  });

  // ── Multiple roles with one Full Access → Full Access ───────────────────────────────
  it('resolves Full Access when at least one of multiple held roles has IsFullAccess=true', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole, fullAccessRole]));
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const component = createAndRender();

    expect(resolvedFullAccess(component)).toBeTrue();
  });

  // ── Multiple normal roles → not Full Access ─────────────────────────────────────────
  it('does not resolve Full Access when holding only multiple normal roles', () => {
    const roleA = makeRole('Epic Integration Manager', false);
    const roleB = makeRole('Reporting Viewer', false);
    setup(makeTestUser([], 'Epic Integration Manager', [roleA, roleB]));
    roleService.getRoles.and.returnValue(of([roleA, roleB]));

    const component = createAndRender();

    expect(resolvedFullAccess(component)).toBeFalse();
    expectNoMovedTabs(tabLabels(component));
  });

  // ── Role lookup/API failure → fail closed ───────────────────────────────────────────
  it('fails closed (no Full Access) when the role lookup errors', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const component = createAndRender();

    expect(resolvedFullAccess(component)).toBeFalse();
    expectNoMovedTabs(tabLabels(component));
  });

  // ── Permission-gated tabs stay governed by their own permissions ────────────────────
  it('leaves every other settings tab governed by its own permissions', () => {
    setup(makeTestUser(['configuration.write', 'ehrendpoints.view'], 'Epic Integration Manager'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = tabLabels(createAndRender());

    expect(labels).toContain('Branding');
    // configuration.write is one of System Settings' own permissions (it has been since this spec was
    // written), so the tab shows.
    expect(labels).toContain('System Settings');
    expect(labels).not.toContain('Workflow Configurations');
    expectNoMovedTabs(labels);
  });

  // EHR Endpoints is a row on System Settings > General now — an ehrendpoints.view-only role must still
  // see the System Settings tab, exactly as settings.routes.ts lets it through that route.
  it('shows System Settings, and only that, for an ehrendpoints.view-only role', () => {
    setup(makeTestUser(['ehrendpoints.view'], 'Epic Integration Manager'));
    roleService.getRoles.and.returnValue(of([]));

    expect(tabLabels(createAndRender())).toEqual(['System Settings']);
  });

  it('still shows every permission-gated tab for isAdmin(), unaffected by this fix', () => {
    setup(makeTestUser([], 'Admin'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = tabLabels(createAndRender());

    expect(labels).toContain('Branding');
    expect(labels).toContain('Workflow Configurations');
    expect(labels).toContain('System Settings');
    expectNoMovedTabs(labels);
  });
});
