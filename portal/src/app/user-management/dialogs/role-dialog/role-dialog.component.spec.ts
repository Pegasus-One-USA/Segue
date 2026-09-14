import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { RoleDialogComponent, RoleDialogData } from './role-dialog.component';
import { IRoleService } from '../../services/i-role.service';
import { AuthService } from '../../../auth/services/auth.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { Role } from '../../../auth/models/user.model';

/**
 * RBAC redesign Step 6: the Full System Access toggle on the role create/edit dialog.
 * `callerHasFullAccess` is resolved from the real per-role IsFullAccess flag (IRoleService.getRoles())
 * cross-referenced against the roles the current session claims to hold (AuthService.roles()) — never
 * a hardcoded SuperAdmin/Admin name check. `IRoleService`/`AuthService`/`PermissionActionGuard` are
 * stubbed here (jasmine spies / lightweight fakes) so this stays a fast, isolated unit test.
 */
describe('RoleDialogComponent', () => {
  const normalRole: Role = {
    id: 'r-normal', name: 'Epic Integration Manager', displayName: 'Epic Integration Manager',
    description: 'A scoped custom role.', permissions: [], color: '#000',
    isSystemRole: false, isFullAccess: false, createdAt: '',
  };

  const fullAccessRole: Role = {
    ...normalRole, id: 'r-full', name: 'Healthcare Platform Admin', displayName: 'Healthcare Platform Admin',
    isFullAccess: true,
  };

  let roleService: jasmine.SpyObj<IRoleService>;

  function setup(data: RoleDialogData, allRoles: Role[], heldRoleNames: string[]) {
    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);
    roleService.getRoles.and.returnValue(of(allRoles));
    roleService.createRole.and.returnValue(of(normalRole));
    roleService.updateRole.and.returnValue(of(normalRole));

    const heldRoles = allRoles.filter(r => heldRoleNames.includes(r.name));

    TestBed.configureTestingModule({
      imports: [RoleDialogComponent],
      providers: [
        { provide: IRoleService, useValue: roleService },
        { provide: AuthService, useValue: { roles: () => heldRoles } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
        { provide: DialogRef, useValue: { close: jasmine.createSpy('close') } },
        { provide: DIALOG_DATA, useValue: data },
      ],
    });

    const fixture = TestBed.createComponent(RoleDialogComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  // RBAC Fix 9: mirrors setup() above but has getRoles() error instead of emit, to cover the fail-closed
  // path — kept as its own small helper rather than adding a branch to setup() itself.
  function setupWithRolesError(data: RoleDialogData, heldRoles: Role[]): RoleDialogComponent {
    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));
    roleService.createRole.and.returnValue(of(normalRole));
    roleService.updateRole.and.returnValue(of(normalRole));

    TestBed.configureTestingModule({
      imports: [RoleDialogComponent],
      providers: [
        { provide: IRoleService, useValue: roleService },
        { provide: AuthService, useValue: { roles: () => heldRoles } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
        { provide: DialogRef, useValue: { close: jasmine.createSpy('close') } },
        { provide: DIALOG_DATA, useValue: data },
      ],
    });

    const fixture = TestBed.createComponent(RoleDialogComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  // RBAC review fix: mirrors setup() above but takes the held Role[] directly instead of deriving it
  // by filtering allRoles by name -- setup()'s heldRoles are literally the same objects as their
  // allRoles counterparts, so they can never exercise a genuine id/displayName divergence between the
  // JWT-held role and the API role list. This lets a test supply two distinct objects for that.
  function setupWithHeldRoles(data: RoleDialogData, allRoles: Role[], heldRoles: Role[]): RoleDialogComponent {
    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);
    roleService.getRoles.and.returnValue(of(allRoles));
    roleService.createRole.and.returnValue(of(normalRole));
    roleService.updateRole.and.returnValue(of(normalRole));

    TestBed.configureTestingModule({
      imports: [RoleDialogComponent],
      providers: [
        { provide: IRoleService, useValue: roleService },
        { provide: AuthService, useValue: { roles: () => heldRoles } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
        { provide: DialogRef, useValue: { close: jasmine.createSpy('close') } },
        { provide: DIALOG_DATA, useValue: data },
      ],
    });

    const fixture = TestBed.createComponent(RoleDialogComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  // ── Real-shape divergence: JWT id (=name placeholder) vs API id (real GUID), plus a divergent
  // displayName -- neither may affect the result; matching is by `name` alone (team lead review
  // comments #1/#2).
  it('a Full Access caller can edit the toggle when matching by name alone, unaffected by a JWT-vs-API id mismatch or a divergent displayName', () => {
    // Mirrors jwt-user.mapper.ts's real shape: the held role's id is the role NAME used as a
    // placeholder, never a real database identifier.
    const heldRole: Role = {
      id: 'Healthcare Platform Admin', name: 'Healthcare Platform Admin', displayName: 'Healthcare Platform Admin',
      description: 'role.', permissions: [], color: '#000', isSystemRole: true, isFullAccess: false, createdAt: '',
    };
    // Mirrors the real API shape (RoleDto): a real database GUID as id, and a displayName that
    // deliberately differs from name.
    const apiFullAccessRole: Role = {
      ...fullAccessRole, id: '3fa85f64-5717-4562-b3fc-2c963f66afa6', displayName: 'Platform Administrator',
    };
    const component = setupWithHeldRoles({ role: normalRole }, [normalRole, apiFullAccessRole], [heldRole]);

    expect(component.callerHasFullAccess()).toBeTrue();
    expect(component.form.controls.isFullAccess.disabled).toBeFalse();
  });

  it('a Full Access caller can edit the toggle', () => {
    const component = setup({ role: normalRole }, [normalRole, fullAccessRole], ['Healthcare Platform Admin']);

    expect(component.callerHasFullAccess()).toBeTrue();
    expect(component.form.controls.isFullAccess.disabled).toBeFalse();
  });

  it('a non-Full-Access caller cannot enable it', () => {
    const component = setup({ role: normalRole }, [normalRole, fullAccessRole], ['Epic Integration Manager']);

    expect(component.callerHasFullAccess()).toBeFalse();
    expect(component.form.controls.isFullAccess.disabled).toBeTrue();
  });

  it('a Full Access role displays Full System Access enabled (checked) when editing it', () => {
    const component = setup({ role: fullAccessRole }, [normalRole, fullAccessRole], ['Healthcare Platform Admin']);

    expect(component.form.controls.isFullAccess.value).toBeTrue();
  });

  it('a normal custom role displays it disabled (unchecked) when editing it', () => {
    const component = setup({ role: normalRole }, [normalRole, fullAccessRole], ['Epic Integration Manager']);

    expect(component.form.controls.isFullAccess.value).toBeFalse();
  });

  it('creating a new role starts with the toggle unchecked, regardless of caller access', () => {
    const component = setup({}, [normalRole, fullAccessRole], ['Healthcare Platform Admin']);

    expect(component.form.controls.isFullAccess.value).toBeFalse();
    expect(component.callerHasFullAccess()).toBeTrue();
    expect(component.form.controls.isFullAccess.disabled).toBeFalse();
  });

  it('a system role keeps the toggle disabled even for a Full Access caller (existing system-role lock is preserved)', () => {
    const systemFullAccessRole: Role = { ...fullAccessRole, id: 'r-admin', name: 'Admin', displayName: 'Admin', isSystemRole: true };
    const component = setup({ role: systemFullAccessRole }, [systemFullAccessRole], ['Admin']);

    expect(component.callerHasFullAccess()).toBeTrue();
    expect(component.form.controls.isFullAccess.disabled).toBeTrue();
  });

  it('save() always sends the toggle current value — unchanged when not editable, backend enforces the rest', () => {
    const component = setup({ role: normalRole }, [normalRole, fullAccessRole], ['Epic Integration Manager']);

    component.save();

    expect(roleService.updateRole).toHaveBeenCalledWith('r-normal', jasmine.objectContaining({ isFullAccess: false }));
  });

  it('save() sends an explicitly-granted isFullAccess=true when a Full Access caller flips the toggle', () => {
    const component = setup({ role: normalRole }, [normalRole, fullAccessRole], ['Healthcare Platform Admin']);

    component.form.controls.isFullAccess.setValue(true);
    component.save();

    expect(roleService.updateRole).toHaveBeenCalledWith('r-normal', jasmine.objectContaining({ isFullAccess: true }));
  });

  // ── RBAC Fix 9: getRoles() failure fails closed, dialog does not throw ─────────────
  it('fails closed (callerHasFullAccess false, toggle stays disabled) when getRoles() errors, without throwing', () => {
    let component!: RoleDialogComponent;

    expect(() => {
      component = setupWithRolesError({ role: normalRole }, [fullAccessRole]);
    }).not.toThrow();

    expect(component.callerHasFullAccess()).toBeFalse();
    expect(component.form.controls.isFullAccess.disabled).toBeTrue();
  });

  it('still allows save() (name/description edits) unaffected by a getRoles() failure', () => {
    const component = setupWithRolesError({ role: normalRole }, [fullAccessRole]);

    component.save();

    expect(roleService.updateRole).toHaveBeenCalledWith('r-normal', jasmine.objectContaining({ isFullAccess: false }));
  });
});
