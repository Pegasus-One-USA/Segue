import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { UserDetailComponent } from './user-detail.component';
import { IUserService } from '../../../auth/services/i-user.service';
import { IRoleService } from '../../services/i-role.service';
import { AuthService } from '../../../auth/services/auth.service';
import { DialogRef, DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { Role, User } from '../../../auth/models/user.model';

/**
 * RBAC Fix 7: the "Disable Two-Factor Authentication" button's availability (`isSuperAdmin`, still named
 * that way so user-detail.component.html needs no change) is now true when the caller is the literal
 * SuperAdmin claim (unchanged) OR holds any role with Full System Access -- resolved the same way
 * settings-shell.component.ts's/system-settings-shell.component.ts's callerHasFullAccess (Fix 5/6) /
 * role-dialog.component.ts do: the real per-role IsFullAccess flag via IRoleService.getRoles(),
 * cross-referenced by name against the roles this session's own claims say it holds. Never a hardcoded
 * role name for the new capability. The actual disableMfa() action/API call is completely untouched.
 */
/** Narrow view exposing the component's own `protected isSuperAdmin` signal for direct assertion in
 *  these tests — avoids `no-explicit-any` while keeping this focused purely on that computed's result. */
interface HarnessedUserDetailComponent {
  isSuperAdmin(): boolean;
}

describe('UserDetailComponent — Disable MFA availability', () => {
  let userService: jasmine.SpyObj<IUserService>;
  let roleService: jasmine.SpyObj<IRoleService>;
  let dialogService: jasmine.SpyObj<DialogService>;
  let authService: {
    hasRole: jasmine.Spy;
    roles: jasmine.Spy;
    isAdmin: jasmine.Spy;
    hasPermission: jasmine.Spy;
    currentUser: jasmine.Spy;
  };

  function makeRole(name: string, isFullAccess: boolean): Role {
    return {
      id: name, name, displayName: name, description: `${name} role.`, permissions: [],
      color: '#000', isSystemRole: false, isFullAccess, createdAt: '',
    };
  }

  function makeUser(overrides: Partial<User> = {}): User {
    return {
      id: 'u-1', email: 'someone@test.local', firstName: 'Some', lastName: 'One', fullName: 'Some One',
      role: 'Epic Integration Manager', roles: [], permissions: [], directPermissionAllocations: [],
      orgId: 'org1', status: 'active', loginType: 'local', mustChangePassword: false,
      emailVerified: true, twoFactorEnabled: true, createdAt: '', updatedAt: '',
      ...overrides,
    };
  }

  function setup(hasSuperAdminClaim: boolean, roles: Role[] = [], isAdmin = false): void {
    userService = jasmine.createSpyObj<IUserService>('IUserService', [
      'getUsers', 'getUser', 'createUser', 'updateUser', 'deleteUser', 'enableUser', 'disableUser',
      'suspendUser', 'assignRole', 'removeRole', 'getRoles', 'getPermissions', 'inviteUser',
      'resendInvitation', 'resetUserPassword', 'disableUserMfa', 'setUserMfaRequirement',
      'getUserPermissionAllocations', 'setUserPermissionAllocation', 'removeUserPermissionAllocation',
      'setUserPermissionAllocations',
    ]);

    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);
    roleService.getPermissionCatalog.and.returnValue(of([]));

    dialogService = jasmine.createSpyObj<DialogService>('DialogService', ['open']);

    authService = {
      hasRole: jasmine.createSpy('hasRole').and.returnValue(hasSuperAdminClaim),
      roles: jasmine.createSpy('roles').and.returnValue(roles),
      isAdmin: jasmine.createSpy('isAdmin').and.returnValue(isAdmin),
      hasPermission: jasmine.createSpy('hasPermission').and.returnValue(false),
      currentUser: jasmine.createSpy('currentUser').and.returnValue(null),
    };

    TestBed.configureTestingModule({
      imports: [UserDetailComponent],
      providers: [
        { provide: IUserService, useValue: userService },
        { provide: IRoleService, useValue: roleService },
        { provide: AuthService, useValue: authService },
        { provide: MatDialog, useValue: jasmine.createSpyObj('MatDialog', ['open']) },
        { provide: DialogService, useValue: dialogService },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['success', 'error', 'warning', 'info']) },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) },
        { provide: PermissionActionGuard, useValue: jasmine.createSpyObj('PermissionActionGuard', ['ensure']) },
      ],
    });
  }

  function createAndInit(): UserDetailComponent {
    const fixture = TestBed.createComponent(UserDetailComponent);
    // `id` left unset -- ngOnInit's `if (this.id) loadUser(...)` is skipped, so getUser() need not be
    // stubbed for these tests, which only care about isSuperAdmin/Disable-MFA availability.
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  // ── SuperAdmin → Disable MFA remains available ──────────────────────────────────────
  it('keeps Disable MFA available for the existing SuperAdmin, without ever calling getRoles()', () => {
    setup(true);

    const component = createAndInit();

    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeTrue();
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  // ── Custom Full Access role → Disable MFA available ─────────────────────────────────
  it('makes Disable MFA available for a custom role with IsFullAccess=true', () => {
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(false, [fullAccessRole]);
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const component = createAndInit();

    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeTrue();
  });

  // ── Normal custom role → Disable MFA remains unavailable ────────────────────────────
  it('keeps Disable MFA unavailable for a normal custom role (IsFullAccess=false)', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(false, [normalRole]);
    roleService.getRoles.and.returnValue(of([normalRole]));

    const component = createAndInit();

    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeFalse();
  });

  // ── Real-shape divergence: JWT id (=name placeholder) vs API id (real GUID), plus a divergent
  // displayName -- neither may affect the result; matching is by `name` alone (team lead review
  // comments #1/#2). The plain makeRole() helper above can't exercise this since it sets
  // id === name === displayName on both sides.
  it('makes Disable MFA available when matching by name alone, unaffected by a JWT-vs-API id mismatch or a divergent displayName', () => {
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
    setup(false, [heldRole]);
    roleService.getRoles.and.returnValue(of([apiRole]));

    const component = createAndInit();

    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeTrue();
  });

  // ── Multiple roles with one Full Access → available ─────────────────────────────────
  it('makes Disable MFA available when at least one of multiple held roles has IsFullAccess=true', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(false, [normalRole, fullAccessRole]);
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const component = createAndInit();

    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeTrue();
  });

  // ── Multiple normal roles → unavailable ──────────────────────────────────────────────
  it('keeps Disable MFA unavailable when holding only multiple normal roles', () => {
    const roleA = makeRole('Epic Integration Manager', false);
    const roleB = makeRole('Reporting Viewer', false);
    setup(false, [roleA, roleB]);
    roleService.getRoles.and.returnValue(of([roleA, roleB]));

    const component = createAndInit();

    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeFalse();
  });

  // ── getRoles() failure → fails closed/unavailable ───────────────────────────────────
  it('fails closed (keeps Disable MFA unavailable) when the role lookup errors', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(false, [normalRole]);
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const component = createAndInit();

    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeFalse();
  });

  // ── Existing user-detail behavior remains unchanged ─────────────────────────────────
  it('leaves every other existing gating rule (canEdit/canToggle/hasStatusMenu) unaffected', () => {
    setup(false, [], /* isAdmin */ true);
    roleService.getRoles.and.returnValue(of([]));

    const component = createAndInit();

    expect(component.canEdit()).toBeTrue();
    expect(component.canToggle()).toBeTrue();
    expect(component.hasStatusMenu()).toBeTrue();
    // isAdmin() alone still never satisfies isSuperAdmin -- unchanged from before this fix.
    expect((component as unknown as HarnessedUserDetailComponent).isSuperAdmin()).toBeFalse();
  });

  // ── The actual Disable-MFA action/API invocation remains unchanged ─────────────────
  it('still calls IUserService.disableUserMfa on confirm, unaffected by the availability gate', () => {
    setup(true); // isSuperAdmin() true here is irrelevant to disableMfa()'s own body/action, only to its visibility
    const targetUser = makeUser();
    userService.disableUserMfa.and.returnValue(of({ ...targetUser, twoFactorEnabled: false }));
    dialogService.open.and.returnValue({ afterClosed: () => of(true) } as unknown as DialogRef<unknown>);

    const component = createAndInit();
    component.user.set(targetUser);

    component.disableMfa();

    expect(dialogService.open).toHaveBeenCalled();
    expect(userService.disableUserMfa).toHaveBeenCalledWith(targetUser.id);
  });

  it('does not call disableUserMfa when the confirmation dialog is dismissed', () => {
    setup(true);
    const targetUser = makeUser();
    dialogService.open.and.returnValue({ afterClosed: () => of(false) } as unknown as DialogRef<unknown>);

    const component = createAndInit();
    component.user.set(targetUser);

    component.disableMfa();

    expect(userService.disableUserMfa).not.toHaveBeenCalled();
  });
});
