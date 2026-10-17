import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConnectionListTableComponent } from './connection-list-table.component';
import { ConnectionListColumn, ConnectionRow, ConnectionRowAction } from '../../connection-row.model';

describe('ConnectionListTableComponent', () => {
  const ROWS: ConnectionRow[] = [
    {
      kind: 'ehr-read', key: 'ehr-read:1', id: '1', name: 'Epic prod', typeLabel: 'Epic', filterKeys: [], audience: null,
      address: 'https://epic.example', clientId: null, writeApis: null, isEnabled: true, actionBy: null, actionOn: null, raw: null,
    },
    {
      kind: 'database', key: 'database:2', id: '2', name: 'Clinic DB', typeLabel: 'SQL database (PostgreSQL)', filterKeys: [],
      audience: null, address: null, clientId: null, writeApis: null, isEnabled: null, actionBy: null, actionOn: null, raw: null,
    },
  ];
  const COLUMNS: ConnectionListColumn[] = [
    { id: 'name', header: 'Name', sortKey: 'name' },
    { id: 'type', header: 'Type', sortKey: 'type' },
    { id: 'clientId', header: 'Client ID' },
    { id: 'status', header: 'Status', sortKey: 'status' },
  ];

  function create(actionsFor: (row: ConnectionRow) => ConnectionRowAction[], total = ROWS.length) {
    TestBed.configureTestingModule({ imports: [ConnectionListTableComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(ConnectionListTableComponent);
    fixture.componentRef.setInput('rows', ROWS);
    fixture.componentRef.setInput('columns', COLUMNS);
    fixture.componentRef.setInput('sort', { key: 'name', dir: 'asc' });
    fixture.componentRef.setInput('testIdPrefix', 'src');
    fixture.componentRef.setInput('actionsFor', actionsFor);
    fixture.componentRef.setInput('total', total);
    fixture.componentRef.setInput('emptyTitle', 'Nothing here');
    fixture.detectChanges();
    return { fixture, table: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  const EDIT: ConnectionRowAction = { id: 'edit', label: 'Edit', icon: 'edit', disabled: false };

  it('marks rows with their name and kind', () => {
    const { el } = create(() => [EDIT]);
    const rows = Array.from(el.querySelectorAll('[data-testid="src-row"]'));

    expect(rows.map(r => r.getAttribute('data-row-name'))).toEqual(['Epic prod', 'Clinic DB']);
    expect(rows.map(r => r.getAttribute('data-row-kind'))).toEqual(['ehr-read', 'database']);
  });

  it('shows "—" for an empty cell and for a row with no status', () => {
    const { el } = create(() => [EDIT]);
    const second = el.querySelectorAll('[data-testid="src-row"]')[1];

    expect(second.textContent).toContain('—');
    expect(second.querySelector('.badge')).toBeNull();
    expect(el.querySelector('[data-testid="src-row"] .badge')!.textContent).toContain('Enabled');
  });

  it('has no row menu for a row with no actions', () => {
    const { el } = create(row => (row.kind === 'database' ? [] : [EDIT]));

    expect(el.querySelectorAll('[data-testid="src-row-menu"]').length).toBe(1);
  });

  it('emits the chosen action', () => {
    const { fixture, table, el } = create(() => [EDIT]);
    const emitted = jasmine.createSpy('action');
    table.action.subscribe(emitted);

    (el.querySelector('[data-testid="src-row-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (document.querySelector('[data-testid="src-row-edit"]') as HTMLButtonElement).click();

    expect(emitted).toHaveBeenCalledWith({ row: ROWS[0], id: 'edit' });
  });

  it('emits the sort key of a sortable header only', () => {
    const { table, el } = create(() => []);
    const sorted = jasmine.createSpy('sort');
    table.sortChange.subscribe(sorted);
    const headers = Array.from(el.querySelectorAll('th')) as HTMLElement[];

    headers.find(h => h.textContent!.includes('Type'))!.click();
    headers.find(h => h.textContent!.includes('Client ID'))!.click();

    expect(sorted.calls.allArgs()).toEqual([['type']]);
  });

  it('shows the empty state when the total is 0', () => {
    const { el } = create(() => [], 0);

    expect(el.querySelector('.empty-state')!.textContent).toContain('Nothing here');
  });
});
