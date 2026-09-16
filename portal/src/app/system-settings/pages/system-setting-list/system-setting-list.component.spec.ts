import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { SystemSettingListComponent } from './system-setting-list.component';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { IRoleService } from '../../../user-management/services/i-role.service';
import { Role, User } from '../../../auth/models/user.model';
import { makeTestUser } from '../../../auth/testing/auth-test-helpers';

/**
 * RBAC Fix 6, retargeted: this coverage used to live on SystemSettingsShellComponent, where
 * General/Security/SSO Configurations were `superAdminOnly` TABS. Those sections are launcher ROWS on this
 * page now (each opened as a dialog, keeping the exact permission its tab had), so the identical rule is
 * enforced here instead — visible for the literal SuperAdmin claim OR any role with Full System Access,
 * resolved by role NAME via IRoleService.getRoles(), never a hardcoded role name.
 *
 * Also covers the split this page introduced: a viewer holding only ehrendpoints.view reaches the page for
 * the EHR Endpoints row alone and must not see the configuration key/value groups.
 */
describe('SystemSettingListComponent', () => {
  let roleService: jasmine.SpyObj<IRoleService>;
  let settingsService: jasmine.SpyObj<ISystemSettingsService>;
  let authStore: AuthStore;

  function makeRole(name: string, isFullAccess: boolean): Role {
    return {
      id: name, name, displayName: name, description: 'Test role.', permissions: [],
      color: '#000', isSystemRole: false, isFullAccess, createdAt: '',
    };
  }

  function setup(user: User | null): void {
    roleService = jasmine.createSpyObj<IRoleService>('IRoleService', [
      'getRoles', 'getPagedRoles', 'getRole', 'createRole', 'updateRole', 'deleteRole',
      'getPermissions', 'getPermissionCatalog',
    ]);
    settingsService = jasmine.createSpyObj<ISystemSettingsService>('ISystemSettingsService', [
      'getAll', 'set', 'setBatch', 'delete', 'decryptProvisionedSecret',
    ]);
    // One ordinary operational group, so "sees the setting groups" is observable.
    settingsService.getAll.and.returnValue(of([
      { key: 'Caching:TerminologyTtlMinutes', value: '60', description: 'TTL.' } as never,
    ]));

    TestBed.configureTestingModule({
      imports: [SystemSettingListComponent],
      providers: [
        AuthStore,
        provideRouter([]),
        { provide: IRoleService, useValue: roleService },
        { provide: ISystemSettingsService, useValue: settingsService },
      ],
    });

    authStore = TestBed.inject(AuthStore);
    authStore.setUser(user);
  }

  function createAndRender(): SystemSettingListComponent {
    const fixture = TestBed.createComponent(SystemSettingListComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  /** Labels of the launcher rows currently rendered. */
  function launcherLabels(component: SystemSettingListComponent): string[] {
    return component.groupedRows()
      .filter(row => 'isLauncher' in row)
      .map(row => (row as { label: string }).label);
  }

  const SUPER_ADMIN_ONLY_LABELS = ['License', 'Allowed Origins', 'Security', 'SSO Configurations'];

  // SuperAdmin -> restricted launcher rows visible, with no extra role lookup.
  it('shows the SuperAdmin-only rows for the existing SuperAdmin, without ever calling getRoles()', () => {
    setup(makeTestUser([], 'SuperAdmin'));

    const labels = launcherLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).toContain(label);
    }
    expect(roleService.getRoles).not.toHaveBeenCalled();
  });

  it('shows the SuperAdmin-only rows for a custom role with IsFullAccess=true', () => {
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Healthcare Platform Admin', [fullAccessRole]));
    roleService.getRoles.and.returnValue(of([fullAccessRole]));

    const labels = launcherLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).toContain(label);
    }
  });

  it('hides the SuperAdmin-only rows for a normal custom role (IsFullAccess=false)', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(of([normalRole]));

    const labels = launcherLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
  });

  // Matching is by role `name` alone: the held role's id is a name placeholder (jwt-user.mapper.ts) while
  // the API returns a real GUID and may carry a divergent displayName. Neither may affect the outcome.
  it('matches by name alone, unaffected by a JWT-vs-API id mismatch or a divergent displayName', () => {
    const heldRole: Role = {
      id: 'Healthcare Platform Admin', name: 'Healthcare Platform Admin', displayName: 'Healthcare Platform Admin',
      description: 'role.', permissions: [], color: '#000', isSystemRole: true, isFullAccess: false, createdAt: '',
    };
    const apiRole: Role = {
      id: '3fa85f64-5717-4562-b3fc-2c963f66afa6', name: 'Healthcare Platform Admin', displayName: 'Platform Administrator',
      description: 'role.', permissions: [], color: '#000', isSystemRole: false, isFullAccess: true, createdAt: '',
    };
    setup(makeTestUser([], 'Healthcare Platform Admin', [heldRole]));
    roleService.getRoles.and.returnValue(of([apiRole]));

    const labels = launcherLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).toContain(label);
    }
  });

  it('shows the rows when at least one of multiple held roles has IsFullAccess=true', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    const fullAccessRole = makeRole('Healthcare Platform Admin', true);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole, fullAccessRole]));
    roleService.getRoles.and.returnValue(of([normalRole, fullAccessRole]));

    const labels = launcherLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).toContain(label);
    }
  });

  it('fails closed (hides the rows) when the role lookup errors', () => {
    const normalRole = makeRole('Epic Integration Manager', false);
    setup(makeTestUser([], 'Epic Integration Manager', [normalRole]));
    roleService.getRoles.and.returnValue(throwError(() => new Error('network error')));

    const labels = launcherLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
  });

  it('still hides the SuperAdmin-only rows for plain Admin, while showing permission-gated ones', () => {
    setup(makeTestUser([], 'Admin'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = launcherLabels(createAndRender());

    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
    // isAdmin() unlocks permission-gated rows but never superAdminOnly ones.
    expect(labels).toContain('Email');
  });

  it('leaves the permission-based visibility of Email unaffected', () => {
    setup(makeTestUser(['configuration.view'], 'Epic Integration Manager'));
    roleService.getRoles.and.returnValue(of([]));

    const labels = launcherLabels(createAndRender());

    expect(labels).toContain('Email');
    for (const label of SUPER_ADMIN_ONLY_LABELS) {
      expect(labels).not.toContain(label);
    }
  });

  it('hides Email for a role holding none of its permissions', () => {
    setup(makeTestUser([], 'Epic Integration Manager', [makeRole('Epic Integration Manager', false)]));
    roleService.getRoles.and.returnValue(of([makeRole('Epic Integration Manager', false)]));

    expect(launcherLabels(createAndRender())).not.toContain('Email');
  });

  // EHR Endpoints is the reason a role without any configuration.* permission can reach this page at all.
  it('shows only the EHR Endpoints row, and no setting groups, for an ehrendpoints.view-only role', () => {
    setup(makeTestUser(['ehrendpoints.view'], 'Epic Integration Manager', [makeRole('Epic Integration Manager', false)]));
    roleService.getRoles.and.returnValue(of([makeRole('Epic Integration Manager', false)]));

    const component = createAndRender();

    expect(launcherLabels(component)).toEqual(['EHR Endpoints']);
    // The configuration key/value groups are not theirs to see, even though the page loaded them.
    expect(component.groupedRows().every(row => 'isLauncher' in row)).toBeTrue();
  });

  it('shows the setting groups for a configuration.view holder', () => {
    setup(makeTestUser(['configuration.view'], 'Epic Integration Manager'));
    roleService.getRoles.and.returnValue(of([]));

    const component = createAndRender();

    expect(component.groupedRows().some(row => !('isLauncher' in row))).toBeTrue();
  });
});
