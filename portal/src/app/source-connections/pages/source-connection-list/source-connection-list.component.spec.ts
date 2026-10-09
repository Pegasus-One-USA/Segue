import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { throwError } from 'rxjs';
import { SourceConnectionListComponent } from './source-connection-list.component';
import { EhrReadConnectionKindComponent } from '../../components/ehr-read-connection-kind/ehr-read-connection-kind.component';
import { DatabaseConnectionKindComponent } from '../../components/database-connection-kind/database-connection-kind.component';
import { ToastService } from '../../../services/toast.service';
import { configureFakeKind, fakeKindComponent, fakeRow } from '../../../connections/testing/fake-connection-kind';
import { ConnectionKindCard } from '../../../connections/connection-row.model';

/** The Source Connections page only merges each kind's rows and dispatches to the kind that owns a row. */
describe('SourceConnectionListComponent', () => {
  const FakeEhr = fakeKindComponent('ehr-read', 'app-ehr-read-connection-kind');
  const FakeDb = fakeKindComponent('database', 'app-database-connection-kind');
  const EPIC = fakeRow('ehr-read', 'e1', 'Epic prod');
  const DB = fakeRow('database', 'd1', 'Clinic DB', { isEnabled: null });
  const CARD: ConnectionKindCard = { kind: 'database', value: 'Database', label: 'SQL database', sub: '', abbr: 'SQL', color: 'x' };

  let toast: { success: jasmine.Spy; error: jasmine.Spy };

  function create() {
    toast = { success: jasmine.createSpy('success'), error: jasmine.createSpy('error') };
    TestBed.configureTestingModule({
      imports: [SourceConnectionListComponent],
      providers: [provideNoopAnimations(), { provide: ToastService, useValue: toast }],
    });
    TestBed.overrideComponent(SourceConnectionListComponent, {
      remove: { imports: [EhrReadConnectionKindComponent, DatabaseConnectionKindComponent] },
      add: { imports: [FakeEhr, FakeDb] },
    });
    const fixture = TestBed.createComponent(SourceConnectionListComponent);
    fixture.detectChanges();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      fixture, page: fixture.componentInstance, el,
      rowNames: () => Array.from(el.querySelectorAll('[data-testid="src-row"]')).map(r => r.getAttribute('data-row-name')),
    };
  }

  it('merges both kinds into one list, the total covering both', () => {
    configureFakeKind('ehr-read', [EPIC]);
    configureFakeKind('database', [DB]);
    const { page, rowNames } = create();

    expect(rowNames()).toEqual(['Clinic DB', 'Epic prod']);
    expect(page.result().total).toBe(2);
  });

  it('filters by kind', () => {
    configureFakeKind('ehr-read', [EPIC]);
    configureFakeKind('database', [DB]);
    const { fixture, page, rowNames } = create();

    page.onFilters({ kind: 'database', vendor: '', audience: '', status: '' });
    fixture.detectChanges();

    expect(rowNames()).toEqual(['Clinic DB']);
  });

  it('never asks a kind the role cannot list, and hides the Kind filter', () => {
    configureFakeKind('ehr-read', [EPIC]);
    const db = configureFakeKind('database', [DB], { canList: false });
    const { el, rowNames } = create();

    expect(db.listCalls).toBe(0);
    expect(rowNames()).toEqual(['Epic prod']);
    expect(el.querySelector('[data-testid="src-filter-kind"]')).toBeNull();
  });

  it('still shows the other kind when one fails, with that kind\'s toast', () => {
    configureFakeKind('ehr-read', [], { list: () => throwError(() => new Error('down')) });
    configureFakeKind('database', [DB]);
    const { rowNames } = create();

    expect(rowNames()).toEqual(['Clinic DB']);
    expect(toast.error).toHaveBeenCalledWith('ehr-read failed');
  });

  it('hides New when no kind offers a card, and opens the picker otherwise', () => {
    configureFakeKind('ehr-read', [EPIC]);
    configureFakeKind('database', [DB]);
    expect(create().el.querySelector('[data-testid="src-add"]')).toBeNull();

    TestBed.resetTestingModule();
    configureFakeKind('ehr-read', [EPIC]);
    const db = configureFakeKind('database', [DB], { cards: [CARD] });
    const { fixture, el } = create();
    (el.querySelector('[data-testid="src-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (el.querySelector('[data-testid="src-vendor-card"][data-kind="database"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(db.created).toEqual([CARD]);
    expect(el.querySelector('app-connection-kind-picker')).toBeNull();
  });

  it('sends a row action to the kind that owns the row', () => {
    const ehr = configureFakeKind('ehr-read', [EPIC]);
    const db = configureFakeKind('database', [DB]);
    const { page } = create();

    page.onAction({ row: DB, id: 'test' });

    expect(db.ran).toEqual([{ id: 'test', row: DB }]);
    expect(ehr.ran).toEqual([]);
  });

  it('reloads only the kind that changed', () => {
    const ehr = configureFakeKind('ehr-read', [EPIC]);
    const db = configureFakeKind('database', [DB]);
    const { fixture } = create();

    fixture.debugElement.query(By.css('app-database-connection-kind')).componentInstance.changed.emit();

    expect(db.listCalls).toBe(2);
    expect(ehr.listCalls).toBe(1);
  });

  it('searches in the browser and resets to the first page', () => {
    configureFakeKind('ehr-read', [EPIC]);
    configureFakeKind('database', [DB]);
    const { fixture, page, rowNames } = create();
    page.pageIndex.set(3);

    page.onSearch('clinic');
    fixture.detectChanges();

    expect(page.pageIndex()).toBe(0);
    expect(rowNames()).toEqual(['Clinic DB']);
  });

  it('starts "Action on" newest first and flips the same column', () => {
    configureFakeKind('ehr-read', [EPIC]);
    configureFakeKind('database', [DB]);
    const { page } = create();

    page.onSort('actionOn');
    expect(page.sort()).toEqual({ key: 'actionOn', dir: 'desc' });
    page.onSort('actionOn');
    expect(page.sort()).toEqual({ key: 'actionOn', dir: 'asc' });
    page.onSort('type');
    expect(page.sort()).toEqual({ key: 'type', dir: 'asc' });
  });

  it('pages the merged list across kinds and steps back when the last row of the last page goes', () => {
    const named = (kind: 'ehr-read' | 'database', numbers: number[]) =>
      numbers.map(n => fakeRow(kind, `${kind}-${n}`, `Conn ${String(n).padStart(2, '0')}`));
    configureFakeKind('ehr-read', named('ehr-read', [1, 2, 3, 4, 5, 6, 12]));
    configureFakeKind('database', named('database', [7, 8, 9, 10, 11]));
    const { fixture, page, rowNames } = create();

    page.onPage({ pageIndex: 1, pageSize: 10 });
    fixture.detectChanges();

    expect(page.pageSize()).toBe(10);
    expect(page.result().total).toBe(12);
    expect(rowNames()).toEqual(['Conn 11', 'Conn 12']);
    expect(page.result().items.map(r => r.kind)).toEqual(['database', 'ehr-read']);

    // Two database rows go (deleted elsewhere): page 2 no longer exists, so the list shows page 1 ...
    configureFakeKind('database', named('database', [7, 8, 9]));
    fixture.debugElement.query(By.css('app-database-connection-kind')).componentInstance.changed.emit();
    fixture.detectChanges();
    expect(page.result().pageIndex).toBe(0);
    expect(page.result().total).toBe(10);

    // ... and the next action carries on from the page actually shown.
    page.onAction({ row: page.result().items[0], id: 'view' });
    expect(page.pageIndex()).toBe(0);
  });
});
