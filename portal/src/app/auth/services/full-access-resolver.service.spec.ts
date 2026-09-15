import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { firstValueFrom } from 'rxjs';
import { FullAccessResolverService } from './full-access-resolver.service';
import { IRoleService } from '../../user-management/services/i-role.service';
import { Role } from '../models/user.model';

/**
 * RBAC review fix: FullAccessResolverService is the single place the "does this set of held role
 * NAMES include a Full Access role" check now lives, replacing the near-identical logic previously
 * duplicated across super-admin.guard.ts, settings-landing.guard.ts, settings-shell.component.ts,
 * system-settings-shell.component.ts, user-detail.component.ts, and role-dialog.component.ts.
 */
describe('FullAccessResolverService', () => {
  let roleService: jasmine.SpyObj<IRoleService>;
  let service: FullAccessResolverService;

  function makeRole(overrides: Partial<Role> & { name: string }): Role {
    return {
      id: overrides.name, displayName: overrides.name, description: `${overrides.name} role.`,
      permissions: [], color: '#000', isSystemRole: false, isFullAccess: false, createdAt: '',
      ...overrides,
    };
  }

  beforeEach(() => {
    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);

    TestBed.configureTestingModule({
      providers: [{ provide: IRoleService, useValue: roleService }],
    });

    service = TestBed.inject(FullAccessResolverService);
  });

  // ── Basic name match → Full Access detected ─────────────────────────────────────────
  it('resolves true when a held role name matches an API role name that has IsFullAccess=true', async () => {
    const fullAccessRole = makeRole({ name: 'Healthcare Platform Admin', isFullAccess: true });
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const result = await firstValueFrom(service.resolve(new Set(['Healthcare Platform Admin'])));

    expect(result).toBeTrue();
  });

  // ── displayName divergence must not affect the result ───────────────────────────────
  it('still resolves true when the API role\'s displayName differs from its name', async () => {
    const fullAccessRole = makeRole({
      name: 'Healthcare Platform Admin', displayName: 'Platform Administrator', isFullAccess: true,
    });
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const result = await firstValueFrom(service.resolve(new Set(['Healthcare Platform Admin'])));

    expect(result).toBeTrue();
  });

  // ── id-space divergence (JWT id=name-placeholder vs API id=real GUID) must not affect the result ──
  it('matches by name alone -- a JWT-shaped held-role id (the role name) never matching the API role\'s real GUID id does not cause a false negative', async () => {
    // Mirrors the real API shape (api-user.service.ts / RoleDto mapping): id is a real database GUID,
    // completely disjoint from the held role's own id (which, per jwt-user.mapper.ts, is the role NAME
    // used as a placeholder -- never a real identifier). Only `name` is ever compared.
    const apiRole = makeRole({
      id: '3fa85f64-5717-4562-b3fc-2c963f66afa6', name: 'Healthcare Platform Admin', isFullAccess: true,
    });
    roleService.getRoles.and.returnValue(of([apiRole]));

    // heldRoleNames is built purely from names (e.g. `store.roles().map(r => r.name)`) -- id is
    // irrelevant to this call's contract, but the scenario this guards against is a caller who (bug)
    // tried comparing ids instead: apiRole.id ('3fa85f64-...') would never equal the held role's own
    // id ('Healthcare Platform Admin' per jwt-user.mapper.ts). Comparing by name sidesteps that entirely.
    const result = await firstValueFrom(service.resolve(new Set(['Healthcare Platform Admin'])));

    expect(result).toBeTrue();
  });

  // ── Normal (non-Full-Access) roles remain denied ─────────────────────────────────────
  it('resolves false for a held role name that matches an API role with IsFullAccess=false', async () => {
    const normalRole = makeRole({ name: 'Epic Integration Manager', isFullAccess: false });
    roleService.getRoles.and.returnValue(of([normalRole]));

    const result = await firstValueFrom(service.resolve(new Set(['Epic Integration Manager'])));

    expect(result).toBeFalse();
  });

  it('resolves false when no held role name matches any API role name at all', async () => {
    const fullAccessRole = makeRole({ name: 'Healthcare Platform Admin', isFullAccess: true });
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const result = await firstValueFrom(service.resolve(new Set(['Epic Integration Manager'])));

    expect(result).toBeFalse();
  });

  it('resolves true when at least one of several held role names matches a Full Access API role', async () => {
    const normalRole = makeRole({ name: 'Epic Integration Manager', isFullAccess: false });
    const fullAccessRole = makeRole({ name: 'Healthcare Platform Admin', isFullAccess: true });
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const result = await firstValueFrom(
      service.resolve(new Set(['Epic Integration Manager', 'Healthcare Platform Admin'])));

    expect(result).toBeTrue();
  });

  it('resolves false for an empty held-role-names set', async () => {
    const fullAccessRole = makeRole({ name: 'Healthcare Platform Admin', isFullAccess: true });
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const result = await firstValueFrom(service.resolve(new Set()));

    expect(result).toBeFalse();
  });

  // ── getRoles() failure fails closed ──────────────────────────────────────────────────
  it('fails closed (resolves false, never errors) when getRoles() errors', async () => {
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const result = await firstValueFrom(service.resolve(new Set(['Healthcare Platform Admin'])));

    expect(result).toBeFalse();
  });
});
