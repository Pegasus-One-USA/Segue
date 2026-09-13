import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { SimpleChange } from '@angular/core';
import { of } from 'rxjs';
import { RolePermissionsComponent } from './role-permissions.component';
import { IRoleService } from '../../services/i-role.service';
import { AuthService } from '../../../auth/services/auth.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { ToastService } from '../../../services/toast.service';
import { Role } from '../../../auth/models/user.model';

/**
 * RBAC redesign Step 6: when a role has Full System Access (Role.isFullAccess), this screen replaces
 * the granular permission matrix with a read-only banner instead — there's nothing meaningful left to
 * select, since CachedUserPermissionsProvider already grants such a role every current and future
 * permission (Step 2). This screen never grants or revokes isFullAccess itself (save() never sets it —
 * see UpdateRoleRequest.isFullAccess); that only happens via role-dialog.component.ts's toggle.
 */
describe('RolePermissionsComponent', () => {
  const base = {
    description: '', permissions: [] as Role['permissions'], color: '#000', createdAt: '',
  };

  const normalRole: Role = { ...base, id: 'r1', name: 'Epic Integration Manager', displayName: 'Epic Integration Manager', isSystemRole: false, isFullAccess: false };
  const fullAccessCustomRole: Role = { ...base, id: 'r2', name: 'Healthcare Platform Admin', displayName: 'Healthcare Platform Admin', isSystemRole: false, isFullAccess: true };
  const superAdminRole: Role = { ...base, id: 'r3', name: 'SuperAdmin', displayName: 'SuperAdmin', isSystemRole: true, isFullAccess: true };
  const adminRole: Role = { ...base, id: 'r4', name: 'Admin', displayName: 'Admin', isSystemRole: true, isFullAccess: true };

  const allRoles = [normalRole, fullAccessCustomRole, superAdminRole, adminRole];

  function setup(targetRole: Role) {
    const roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);
    roleService.getRoles.and.returnValue(of(allRoles));
    roleService.getPermissionCatalog.and.returnValue(of([]));

    TestBed.configureTestingModule({
      imports: [RolePermissionsComponent],
      providers: [
        { provide: IRoleService, useValue: roleService },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['success', 'error', 'warning', 'info']) },
        { provide: AuthService, useValue: { isAdmin: () => false, hasPermission: () => true } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
      ],
    });

    const fixture = TestBed.createComponent(RolePermissionsComponent);
    const component = fixture.componentInstance;
    component.id = targetRole.id;
    component.ngOnChanges({ id: new SimpleChange(undefined, targetRole.id, true) });
    fixture.detectChanges();
    return { fixture, component };
  }

  it('a Full Access role displays Full System Access enabled — banner shown, matrix hidden', () => {
    const { fixture, component } = setup(fullAccessCustomRole);

    expect(component.isFullAccessRole()).toBeTrue();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.full-access-notice')).toBeTruthy();
    expect(el.querySelector('.permissions-card')).toBeFalsy();
  });

  it('a normal custom role displays it disabled — no banner, matrix still shown', () => {
    const { fixture, component } = setup(normalRole);

    expect(component.isFullAccessRole()).toBeFalse();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.full-access-notice')).toBeFalsy();
    expect(el.querySelector('.permissions-card')).toBeTruthy();
  });

  it('existing granular permissions still work for a normal role (matrix computed signals unaffected)', () => {
    const { component } = setup(normalRole);

    expect(component.isFullAccessRole()).toBeFalse();
    // Empty catalog in this test fixture, but the computed chain must still resolve without throwing —
    // proves Step 6 didn't alter the existing matrix computation path for a non-full-access role.
    expect(component.allPermissions()).toEqual([]);
    expect(component.selectedIds().size).toBe(0);
  });

  it('existing SuperAdmin behavior is preserved — still permissions-locked, and now also shows the Full Access banner', () => {
    const { component } = setup(superAdminRole);

    expect(component.isPermissionsLocked()).toBeTrue();
    expect(component.isFullAccessRole()).toBeTrue();
  });

  it('existing Admin behavior is preserved — never permissions-locked by name, and now also shows the Full Access banner', () => {
    const { component } = setup(adminRole);

    expect(component.isPermissionsLocked()).toBeFalse();
    expect(component.isFullAccessRole()).toBeTrue();
  });
});
