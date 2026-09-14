import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, UrlTree } from '@angular/router';
import { firstValueFrom, Observable, of, throwError } from 'rxjs';
import { superAdminGuard } from './super-admin.guard';
import { AuthStore } from '../store/auth.store';
import { IRoleService } from '../../user-management/services/i-role.service';
import { Role } from '../models/user.model';
import { makeTestUser } from '../testing/auth-test-helpers';

/**
 * RBAC Fix 3: superAdminGuard now allows a caller through when they are the literal SuperAdmin claim
 * (unchanged, zero API calls) OR when any role they hold has Full System Access -- resolved via
 * IRoleService.getRoles() cross-referenced by name against the roles the session's own claims say it
 * holds, the same pattern role-dialog.component.ts's callerHasFullAccess already uses. Never a hardcoded
 * role name for the new capability, and it fails closed (redirects to /unauthorized) on any lookup error.
 */
describe('superAdminGuard', () => {
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

  async function runGuard(): Promise<boolean | UrlTree> {
    const result = TestBed.runInInjectionContext(() =>
      superAdminGuard({} as never, {} as never));

    if (result && typeof (result as Observable<unknown>).subscribe === 'function') {
      return firstValueFrom(result as Observable<boolean | UrlTree>);
    }

    return result as boolean | UrlTree;
  }

  function expectAllowed(result: boolean | UrlTree): void {
    expect(result).toBeTrue();
  }

  function expectUnauthorized(result: boolean | UrlTree): void {
    expect(result).not.toBe(true);
    expect(router.serializeUrl(result as UrlTree)).toBe('/unauthorized');
  }

  // ── Existing SuperAdmin → allowed (unchanged, zero API calls) ───────────────────────
  it('allows the existing SuperAdmin claim, without ever calling getRoles()', async () => {
    authStore.setUser(makeTestUser([], 'SuperAdmin'));

    const result = await runGuard();

    expectAllowed(result);
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  // ── Custom IsFullAccess=true role → allowed ─────────────────────────────────────────
  it('allows a custom role with IsFullAccess=true', async () => {
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    authStore.setUser(makeTestUser([], 'Healthcare Platform Admin', [fullAccessRole]));
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const result = await runGuard();

    expectAllowed(result);
  });

  // ── Normal custom role (IsFullAccess=false) → denied ────────────────────────────────
  it('denies a normal custom role with IsFullAccess=false', async () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(of([normalRole]));

    const result = await runGuard();

    expectUnauthorized(result);
  });

  // ── User with multiple roles, one Full Access → allowed ─────────────────────────────
  it('allows a user holding multiple roles when at least one has IsFullAccess=true', async () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [normalRole, fullAccessRole]));
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const result = await runGuard();

    expectAllowed(result);
  });

  // ── Real-shape divergence: JWT id (=name placeholder) vs API id (real GUID), plus a divergent
  // displayName -- neither may affect the result; matching is by `name` alone (team lead review
  // comments #1/#2). The plain makeRole() helper above can't exercise this since it sets
  // id === name === displayName on both sides.
  it('matches by role name alone -- unaffected by a JWT-vs-API id mismatch or a divergent displayName', async () => {
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
    authStore.setUser(makeTestUser([], 'Healthcare Platform Admin', [heldRole]));
    roleService.getRoles.and.returnValue(of([apiRole]));

    const result = await runGuard();

    expectAllowed(result);
  });

  // ── User with multiple normal roles → denied ────────────────────────────────────────
  it('denies a user holding multiple roles when none has IsFullAccess=true', async () => {
    const roleA = makeRole('Epic Integration Manager', false);
    const roleB = makeRole('Reporting Viewer', false);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [roleA, roleB]));
    roleService.getRoles.and.returnValue(of([roleA, roleB]));

    const result = await runGuard();

    expectUnauthorized(result);
  });

  // ── Role lookup/API failure → denied (fails closed) ─────────────────────────────────
  it('fails closed (denies) when the role lookup errors', async () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    authStore.setUser(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const result = await runGuard();

    expectUnauthorized(result);
  });

  // ── Existing redirect behavior remains unchanged ────────────────────────────────────
  it('redirects an unauthenticated user to /auth/login, exactly as before', async () => {
    authStore.setUser(null);

    const result = await runGuard();

    expect(result).not.toBe(true);
    expect(router.serializeUrl(result as UrlTree)).toBe('/auth/login');
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  it('still redirects a plain non-full-access user to /unauthorized, same outcome as before this fix', async () => {
    authStore.setUser(makeTestUser([], 'Operations'));
    roleService.getRoles.and.returnValue(of([]));

    const result = await runGuard();

    expectUnauthorized(result);
  });
});
