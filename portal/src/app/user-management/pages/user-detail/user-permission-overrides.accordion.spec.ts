import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { UserPermissionOverridesComponent } from './user-permission-overrides.component';
import { IUserService } from '../../../auth/services/i-user.service';
import { IRoleService } from '../../services/i-role.service';
import { AuthService } from '../../../auth/services/auth.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { Permission, PermissionCategory } from '../../../auth/models/user.model';

/**
 * Workflow Configuration in the Settings table is a design-only accordion: the parent row shows no
 * checkboxes, and its three child rows hold local placeholder checkboxes that never reach the
 * user's overrides, Set all or Save.
 */
describe('UserPermissionOverridesComponent — Workflow Configuration accordion', () => {
  const ACTIONS = ['create', 'view'];

  function perm(resource: string, action: string): Permission {
    return {
      id: `${resource}.${action}`, name: `${resource}.${action}`, displayName: `${resource} ${action}`,
      resource, action, description: '',
    };
  }

  function group(name: string, displayName: string) {
    const resource = name.toLowerCase();
    return { id: name, name, displayName, permissions: ACTIONS.map((a) => perm(resource, a)) };
  }

  const catalog: PermissionCategory[] = [{
    id: 'settings', name: 'Settings', displayName: 'Settings',
    groups: [
      group('Branding', 'Branding'),
      group('SystemSettings', 'System Setting'),
      group('WorkflowConfiguration', 'Workflow Configuration'),
    ],
  }];

  let fixture: ComponentFixture<UserPermissionOverridesComponent>;
  let component: UserPermissionOverridesComponent;
  let host: HTMLElement;

  beforeEach(() => {
    const userService = jasmine.createSpyObj<IUserService>('IUserService', ['getUserPermissionAllocations', 'setUserPermissionAllocations']);
    // One existing override, so the grid is editable (not "inherit from role").
    userService.getUserPermissionAllocations.and.returnValue(of([{ permissionId: 'branding.view', permissionName: 'branding.view', permissionDescription: '', isEnabled: true }]));
    const roleService = jasmine.createSpyObj<IRoleService>('IRoleService', ['getPermissionCatalog']);
    roleService.getPermissionCatalog.and.returnValue(of(catalog));

    TestBed.configureTestingModule({
      imports: [UserPermissionOverridesComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: IUserService, useValue: userService },
        { provide: IRoleService, useValue: roleService },
        { provide: AuthService, useValue: { isAdmin: () => true, hasPermission: () => true } },
        { provide: MatDialog, useValue: jasmine.createSpyObj('MatDialog', ['open']) },
        { provide: DialogService, useValue: jasmine.createSpyObj('DialogService', ['open']) },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['success', 'error', 'warning', 'info']) },
        { provide: PermissionActionGuard, useValue: jasmine.createSpyObj('PermissionActionGuard', ['ensure']) },
      ],
    });

    fixture = TestBed.createComponent(UserPermissionOverridesComponent);
    component = fixture.componentInstance;
    component.userId = 'u-1';
    component.ngOnChanges({
      userId: { currentValue: 'u-1', previousValue: undefined, firstChange: true, isFirstChange: () => true },
    });
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  });

  const SYSTEM_SETTING_CHILDREN = [
    'Decrypt Provisioned Secret', 'Terminology Server', 'License', 'EHR Endpoints', 'Allowed Origins',
    'Email', 'Security', 'SSO Configurations', 'Error Log Handling', 'Alert Evaluation',
    'Anomaly Detection', 'Audit Chain Verification', 'Authentication', 'Caching', 'Compliance',
    'Ehr Write Back', 'Endpoint Health Check', 'Generated File Download', 'Incremental Sync', 'Local Auth',
    'Mfa', 'OAuth', 'Rate Limiting', 'Runtime Worker', 'Schedule Dispatcher',
    'Transformation Rules', 'Webhook Ingestion', 'Workflow Setting', 'Workflow Numbering',
  ];
  const WORKFLOW_CONFIGURATION_CHILDREN = ['Source connection', 'Destination connection', 'Mapping Profile'];

  const parentRow = (label: string) =>
    Array.from(host.querySelectorAll<HTMLElement>('tr.permission-grid__parent-row'))
      .find((tr) => tr.textContent!.includes(label))!;
  const toggleOf = (label: string) => parentRow(label).querySelector<HTMLButtonElement>('button.permission-grid__accordion')!;
  // The child rows directly following a parent row, up to the next non-child row.
  const childRowsOf = (label: string): HTMLElement[] => {
    const rows: HTMLElement[] = [];
    let next = parentRow(label).nextElementSibling as HTMLElement | null;
    while (next?.classList.contains('permission-grid__child-row')) {
      rows.push(next);
      next = next.nextElementSibling as HTMLElement | null;
    }
    return rows;
  };
  const childLabelsOf = (label: string) =>
    childRowsOf(label).map((tr) => tr.querySelector('td.permission-grid__group-name')!.textContent!.trim());

  for (const label of ['Workflow Configuration', 'System Setting']) {
    it(`renders ${label} as a parent row with no checkbox or All toggle`, () => {
      expect(parentRow(label)).toBeDefined();
      expect(parentRow(label).querySelectorAll('mat-checkbox, app-tri-state-toggle').length).toBe(0);
    });
  }

  it('shows both accordions expanded by default with their children in order', () => {
    expect(childLabelsOf('Workflow Configuration')).toEqual(WORKFLOW_CONFIGURATION_CHILDREN);
    expect(childLabelsOf('System Setting')).toEqual(SYSTEM_SETTING_CHILDREN);
  });

  it('gives every child row a checkbox per column plus All', () => {
    const children = Array.from(host.querySelectorAll('tr.permission-grid__child-row'));
    expect(children.length).toBe(WORKFLOW_CONFIGURATION_CHILDREN.length + SYSTEM_SETTING_CHILDREN.length);
    children.forEach((tr) => {
      expect(tr.querySelectorAll('mat-checkbox').length).toBe(ACTIONS.length);
      expect(tr.querySelectorAll('app-tri-state-toggle').length).toBe(1);
    });
  });

  it('collapses and expands each accordion independently', () => {
    toggleOf('System Setting').click();
    fixture.detectChanges();
    expect(childLabelsOf('System Setting')).toEqual([]);
    expect(toggleOf('System Setting').getAttribute('aria-expanded')).toBe('false');
    expect(childLabelsOf('Workflow Configuration')).toEqual(WORKFLOW_CONFIGURATION_CHILDREN);

    toggleOf('Workflow Configuration').click();
    fixture.detectChanges();
    expect(childLabelsOf('Workflow Configuration')).toEqual([]);

    toggleOf('System Setting').click();
    fixture.detectChanges();
    expect(childLabelsOf('System Setting')).toEqual(SYSTEM_SETTING_CHILDREN);
    expect(childLabelsOf('Workflow Configuration')).toEqual([]);
  });

  it('orders the parent rows Branding, Workflow Configuration, System Setting', () => {
    // The fixture catalog lists System Setting before Workflow Configuration, as the API does.
    const parents = Array.from(host.querySelectorAll('tbody tr:not(.permission-grid__child-row) td.permission-grid__group-name'))
      .map((td) => td.textContent!.replace('expand_more', '').trim());
    expect(parents).toEqual(['Branding', 'Workflow Configuration', 'System Setting']);
  });

  it('keeps Branding as an ordinary checkbox row', () => {
    const labels = Array.from(host.querySelectorAll('tbody tr:not(.permission-grid__parent-row):not(.permission-grid__child-row) td.permission-grid__group-name'))
      .map((td) => td.textContent!.trim());
    expect(labels).toEqual(['Branding']);
  });

  it('never stages overrides from placeholder checkboxes or either parent row', () => {
    component.setPlaceholderChecked('workflow-configuration.source-connection', 'Create', false);
    component.setPlaceholderRowState('system-settings.license', ['Create', 'View'], 'inherit');
    component.setGlobalState('deny');

    expect(component.isPlaceholderChecked('workflow-configuration.source-connection', 'Create')).toBeFalse();
    const keys = [...component.overrides().keys()];
    // Set all reached only Branding — never either parent's own permissions or a placeholder.
    expect(keys.every((id) => id.startsWith('branding.'))).toBeTrue();
  });
});
