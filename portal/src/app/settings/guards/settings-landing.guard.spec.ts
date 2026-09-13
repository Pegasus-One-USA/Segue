import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, UrlTree } from '@angular/router';
import { firstValueFrom, Observable, of, throwError } from 'rxjs';
import { settingsLandingGuard, SettingsLandingCandidate } from './settings-landing.guard';
import { AuthStore } from '../../auth/store/auth.store';
import { IRoleService } from '../../user-management/services/i-role.service';
import { Role } from '../../auth/models/user.model';
import { makeTestUser } from '../../auth/testing/auth-test-helpers';

/**
 * RBAC Fix 4: settingsLandingGuard's `superAdminOnly` candidates now resolve as reachable when the caller
 * is the literal SuperAdmin claim (unchanged) OR holds any role with Full System Access -- resolved the
 * same way super-admin.guard.ts (RBAC Fix 3) does: IRoleService.getRoles() cross-referenced by name
 * against the roles this session's own claims say it holds, never a hardcoded role name. Every
 * permission-gated candidate's own resolution is completely untouched.
 */
describe('settingsLandingGuard', () => {
  let authStore: AuthStore;
  let roleService: jasmine.SpyObj<IRoleService>;
  let router: Router;

  function makeRole(name: string, isFullAccess: boolean): Role {
    return {
      id: name, name, displayName: name, description: `${name} role.`, permissions: [],
      color: '#000', isSystemRole: false, isFullAccess, createdAt: '',
    };
  }

  beforeEach(() => {
    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);

    TestBed.configureTestingModule({
      providers: [
        AuthStore,
        provideRouter([]),
        { provide: IRoleService, useValue: roleService },
      ],
    });

    authStore = TestBed.inject(AuthStore);
    router = TestBed.inject(Router);
  });

  // Only a superAdminOnly candidate -- isolates exactly the behavior this fix changes.
  const SUPER_ADMIN_ONLY_CANDIDATES: SettingsLandingCandidate[] = [{ path: 'general', superAdminOnly: true }];

  // Mirrors the real, live shape used throughout settings.routes.ts: a permission-gated candidate first,
  // a superAdminOnly one last.
  const REALISTIC_CANDIDATES: SettingsLandingCandidate[] = [
    { path: 'email', permissions: ['configuration.view', 'configuration.write'] },
    { path: 'general', superAdminOnly: true },
  ];

  async function runGuard(candidates: SettingsLandingCandidate[]): Promise<UrlTree> {
    const guard = settingsLandingGuard('/settings/system-settings', candidates);
    const result = TestBed.runInInjectionContext(() => guard({} as never, {} as never));

    const resolved = result && typeof (result as Observable<unknown>).subscribe === 'function'
      ? await firstValueFrom(result as Observable<UrlTree>)
      : (result as UrlTree);

    return resolved;
  }

  function expectPath(result: UrlTree, path: string): void {
    expect(router.serializeUrl(result)).toBe(path);
  }

  // ── Existing SuperAdmin → existing behavior preserved ───────────────────────────────
  it('lands the existing SuperAdmin on the superAdminOnly candidate, without ever calling getRoles()', async () => {
    authStore.setUser(makeTestUser([], 'SuperAdmin'));

    const result = await runGuard(SUPER_ADMIN_ONLY_CANDIDATES);

    expectPath(result, '/settings/system-settings/general');
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  // ── Custom Full Access role → correct settings landing behavior ────────────────────
  it('lands a custom role with IsFullAccess=true on the superAdminOnly candidate', async () => {
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    authStore.setUser(makeTestUser([], 'Healthcare Platform Admin', [fullAccessRole]));
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const result = await runGuard(SUPER_ADMIN_ONLY_CANDIDATES);

    expectPath(result, '/settings/system-settings/general');
  });

  // ── Normal custom role → existing restricted behavior ───────────────────────────────
  it('sends a normal custom role (IsFullAccess=false) to /unauthorized, same as before this fix', async () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(of([normalRole]));

    const result = await runGuard(SUPER_ADMIN_ONLY_CANDIDATES);

    expectPath(result, '/unauthorized');
  });

  // ── Multiple roles with one Full Access → treated as Full Access ───────────────────
  it('treats a user holding multiple roles as elevated when at least one has IsFullAccess=true', async () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [normalRole, fullAccessRole]));
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const result = await runGuard(SUPER_ADMIN_ONLY_CANDIDATES);

    expectPath(result, '/settings/system-settings/general');
  });

  // ── Multiple normal roles → not treated as Full Access ──────────────────────────────
  it('does not treat a user holding only multiple normal roles as elevated', async () => {
    const roleA = makeRole('Epic Integration Manager', false);
    const roleB = makeRole('Reporting Viewer', false);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [roleA, roleB]));
    roleService.getRoles.and.returnValue(of([roleA, roleB]));

    const result = await runGuard(SUPER_ADMIN_ONLY_CANDIDATES);

    expectPath(result, '/unauthorized');
  });

  // ── getRoles() failure → fail closed ────────────────────────────────────────────────
  it('fails closed (routes to /unauthorized) when the role lookup errors', async () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const result = await runGuard(SUPER_ADMIN_ONLY_CANDIDATES);

    expectPath(result, '/unauthorized');
  });

  // ── Unauthenticated user → existing authentication behavior preserved ──────────────
  it('still routes an unauthenticated user to /unauthorized (this guard never issued an /auth/login redirect itself, unchanged)', async () => {
    authStore.setUser(null);
    // An unauthenticated caller matches no permission-gated candidate either, so the outcome still
    // depends on the superAdminOnly candidate -- the real backend would 401 this getRoles() call; the
    // guard must still resolve safely to /unauthorized rather than throwing or hanging.
    roleService.getRoles.and.returnValue(throwError(() => new Error('401 Unauthorized')));

    const result = await runGuard(REALISTIC_CANDIDATES);

    expectPath(result, '/unauthorized');
  });

  // ── Existing permission-based candidates are completely unaffected ──────────────────
  it('still resolves an earlier permission-gated candidate without ever calling getRoles() (existing behavior preserved)', async () => {
    authStore.setUser(makeTestUser(['configuration.write'], 'Epic Integration Manager'));

    const result = await runGuard(REALISTIC_CANDIDATES);

    expectPath(result, '/settings/system-settings/email');
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });
});
