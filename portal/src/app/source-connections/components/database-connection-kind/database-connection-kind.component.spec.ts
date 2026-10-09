import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { DatabaseConnectionKindComponent } from './database-connection-kind.component';
import { TabularSourceService, TabularSqlConnection } from '../../../services/tabular-source.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { ConnectionRow } from '../../../connections/connection-row.model';

/** The databases CSV / SQL Table sources read, as one kind of row on Source Connections. */
describe('DatabaseConnectionKindComponent', () => {
  const PG: TabularSqlConnection = {
    id: 'db1', name: 'Clinic DB', engine: 'postgresql', secretKeyVaultName: 'v', secretName: 's',
    createdBy: 'Ann', updatedOnUtc: '2026-01-01T00:00:00Z',
  };
  const NO_ID: TabularSqlConnection = { ...PG, id: null, name: 'legacy' };
  const NO_NAME: TabularSqlConnection = { ...PG, id: 'db2', name: null };

  let api: { listSqlConnections: jasmine.Spy; testSqlConnection: jasmine.Spy; deleteSqlConnection: jasmine.Spy; saveSqlConnection: jasmine.Spy; updateSqlConnection: jasmine.Spy };
  let toast: { success: jasmine.Spy; error: jasmine.Spy };
  let confirmed: boolean;

  function create(granted: (code: string) => boolean = () => true) {
    confirmed = true;
    api = {
      listSqlConnections: jasmine.createSpy('list').and.returnValue(of([PG, NO_ID, NO_NAME])),
      testSqlConnection: jasmine.createSpy('test').and.returnValue(of({ ok: true, message: 'Connected.' })),
      deleteSqlConnection: jasmine.createSpy('delete').and.returnValue(of(undefined)),
      saveSqlConnection: jasmine.createSpy('save'),
      updateSqlConnection: jasmine.createSpy('update'),
    };
    toast = { success: jasmine.createSpy('success'), error: jasmine.createSpy('error') };
    TestBed.configureTestingModule({
      imports: [DatabaseConnectionKindComponent],
      providers: [
        { provide: TabularSourceService, useValue: api },
        { provide: PermissionService, useValue: { hasPermission: granted, hasAll: (codes: readonly string[]) => codes.every(granted) } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
        { provide: DialogService, useValue: { open: () => ({ afterClosed: () => of(confirmed) }) } },
        { provide: ToastService, useValue: toast },
      ],
    });
    const fixture = TestBed.createComponent(DatabaseConnectionKindComponent);
    fixture.detectChanges();
    return { fixture, kind: fixture.componentInstance };
  }

  function rows(kind: DatabaseConnectionKindComponent): ConnectionRow[] {
    let result: ConnectionRow[] = [];
    kind.list().subscribe(r => (result = r));
    return result;
  }

  it('lists databases with an id, labelled by engine', () => {
    const { kind } = create();
    const list = rows(kind);

    expect(list.map(r => r.id)).toEqual(['db1', 'db2']);
    expect(list[0]).toEqual(jasmine.objectContaining({
      kind: 'database', typeLabel: 'SQL database (PostgreSQL)', isEnabled: null, address: null,
      actionBy: 'Ann', actionOn: '2026-01-01T00:00:00Z', filterKeys: ['kind:database', 'engine:postgresql'],
    }));
    expect(list[1].name).toBe('Unnamed database');
  });

  it('needs tabularsources.view to list, and offers no card without it', () => {
    const { kind } = create(code => code === 'tabularsources.edit');

    expect(kind.canList()).toBeFalse();
    expect(kind.createCards()).toEqual([]);
  });

  it('offers Test and Edit with the edit right, Delete with the delete right', () => {
    expect(create(code => code === 'tabularsources.edit').kind.rowActions().map(a => a.id)).toEqual(['test', 'edit']);
    TestBed.resetTestingModule();
    expect(create(code => code === 'tabularsources.delete').kind.rowActions().map(a => a.id)).toEqual(['delete']);
  });

  it('toasts the test result', () => {
    const { kind } = create();
    const [row] = rows(kind);

    kind.runAction('test', row);
    expect(toast.success).toHaveBeenCalledWith('Connected.');

    api.testSqlConnection.and.returnValue(of({ ok: false, message: 'Login can write.' }));
    kind.runAction('test', row);
    expect(toast.error).toHaveBeenCalledWith('Login can write.');

    api.testSqlConnection.and.returnValue(throwError(() => new Error('down')));
    kind.runAction('test', row);
    expect(toast.error).toHaveBeenCalledWith('The test could not run. Try again.');
  });

  it('deletes after the confirm and reports a change; cancel does nothing', () => {
    const { kind } = create();
    const [row] = rows(kind);
    const changed = jasmine.createSpy('changed');
    kind.changed.subscribe(changed);

    confirmed = false;
    kind.runAction('delete', row);
    expect(api.deleteSqlConnection).not.toHaveBeenCalled();

    confirmed = true;
    kind.runAction('delete', row);
    expect(api.deleteSqlConnection).toHaveBeenCalledWith('db1');
    expect(changed).toHaveBeenCalled();
  });

  it('Edit opens the editor with the saved database', () => {
    const { fixture, kind } = create();
    const [row] = rows(kind);

    kind.runAction('edit', row);
    fixture.detectChanges();

    expect(kind.editingExisting()).toBe(PG);
    expect((fixture.nativeElement as HTMLElement).querySelector('app-tabular-database-editor')).not.toBeNull();
  });

  it('New opens an empty editor', () => {
    const { kind } = create();

    expect(kind.create()).toBeTrue();
    expect(kind.editing()).toBe('new');
    expect(kind.editingExisting()).toBeNull();
  });
});
