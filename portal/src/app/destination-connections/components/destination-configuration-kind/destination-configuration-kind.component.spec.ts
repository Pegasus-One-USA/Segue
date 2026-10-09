import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { DestinationConfigurationKindComponent } from './destination-configuration-kind.component';
import { DestinationConfigurationService } from '../../services/destination-configuration.service';
import { DestinationConfigurationDto } from '../../models/destination-configuration.model';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { ConnectionRow } from '../../../connections/connection-row.model';

/** Destinations as one kind of row on Destination Connections: history / usage locks and the create dialog. */
describe('DestinationConfigurationKindComponent', () => {
  const SQL = {
    id: 'd1', name: 'Warehouse', destinationType: 'SqlServer', keyVaultName: 'v', secretName: 's', target: 'dbo.X',
    isEnabled: true, createdBy: 'Ann', createdOnUtc: '2026-01-01T00:00:00Z',
  } as DestinationConfigurationDto;
  const MONGO = { ...SQL, id: 'd2', name: 'Docs', destinationType: 'Mongo', target: null } as DestinationConfigurationDto;

  let svc: { getAllPages: jasmine.Spy; getUsedInWorkflowIds: jasmine.Spy; hasExecutionHistory: jasmine.Spy; delete: jasmine.Spy };
  let dialog: { open: jasmine.Spy };

  function create(granted: (code: string) => boolean = () => true) {
    svc = {
      getAllPages: jasmine.createSpy('getAllPages').and.returnValue(of([SQL, MONGO])),
      getUsedInWorkflowIds: jasmine.createSpy('used').and.returnValue(of(['d2'])),
      hasExecutionHistory: jasmine.createSpy('history').and.callFake((id: string) => of({ hasExecutionHistory: id === 'd1' })),
      delete: jasmine.createSpy('delete').and.returnValue(of(undefined)),
    };
    dialog = { open: jasmine.createSpy('open').and.returnValue({ afterClosed: () => of(false) }) };
    TestBed.configureTestingModule({
      imports: [DestinationConfigurationKindComponent],
      providers: [
        { provide: DestinationConfigurationService, useValue: svc },
        { provide: PermissionService, useValue: { hasPermission: granted, hasAll: (codes: readonly string[]) => codes.every(granted) } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
        { provide: DialogService, useValue: dialog },
        { provide: ToastService, useValue: { success: () => undefined, error: () => undefined } },
      ],
    });
    const fixture = TestBed.createComponent(DestinationConfigurationKindComponent);
    return { kind: fixture.componentInstance };
  }

  function rows(kind: DestinationConfigurationKindComponent): ConnectionRow[] {
    let result: ConnectionRow[] = [];
    kind.list().subscribe(r => (result = r));
    return result;
  }

  it('lists every destination, page by page from the paged list', () => {
    const { kind } = create();
    const [sql, mongo] = rows(kind);

    expect(svc.getAllPages).toHaveBeenCalled();
    expect(sql).toEqual(jasmine.objectContaining({
      kind: 'destination', typeLabel: 'SQL Server', address: 'dbo.X', isEnabled: true, actionBy: 'Ann',
      filterKeys: ['kind:destination', 'destination:SqlServer'],
    }));
    expect(mongo.address).toBeNull();
  });

  it('cannot list without destinationconnections.view', () => {
    expect(create(code => code !== 'destinationconnections.view').kind.canList()).toBeFalse();
    TestBed.resetTestingModule();
    expect(create().kind.canList()).toBeTrue();
  });

  it('asks for execution history only for rows it has no flag for, silently', () => {
    const { kind } = create();
    const list = rows(kind);

    kind.rowsShown(list);
    kind.rowsShown(list);

    expect(svc.hasExecutionHistory).toHaveBeenCalledTimes(2);
    expect(svc.hasExecutionHistory).toHaveBeenCalledWith('d1', true);
    expect(svc.hasExecutionHistory).toHaveBeenCalledWith('d2', true);
  });

  it('locks Delete by execution history and by workflow use', () => {
    const { kind } = create();
    const [sql, mongo] = rows(kind);
    kind.rowsShown([sql, mongo]);

    const del = (row: ConnectionRow) => kind.rowActions(row).find(a => a.id === 'delete')!;
    expect(del(sql)).toEqual(jasmine.objectContaining({ disabled: true, label: 'Cannot delete — has execution history' }));
    expect(del(mongo)).toEqual(jasmine.objectContaining({ disabled: true, label: 'Cannot delete — referenced by a workflow' }));
    expect(kind.rowActions(sql).find(a => a.id === 'edit')!.label).toBe('View (has execution history)');
  });

  it('opens the create dialog on the chosen type', () => {
    const { kind } = create();
    const card = kind.createCards().find(c => c.value === 'Mongo')!;

    expect(card).toEqual(jasmine.objectContaining({ kind: 'destination', label: 'MongoDB', abbr: 'M' }));
    expect(kind.create(card)).toBeTrue();
    expect(dialog.open.calls.mostRecent().args[1].data).toEqual({ mode: 'create', initialType: 'Mongo' });
  });

  it('offers only the types the role can create', () => {
    const { kind } = create(code => code === 'mongo.create');

    expect(kind.createCards().map(c => c.value)).toEqual(['Mongo']);
  });
});
