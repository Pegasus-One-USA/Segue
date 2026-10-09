import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { throwError } from 'rxjs';
import { DestinationConnectionListComponent } from './destination-connection-list.component';
import { DestinationConfigurationKindComponent } from '../../components/destination-configuration-kind/destination-configuration-kind.component';
import { EhrWriteConnectionKindComponent } from '../../components/ehr-write-connection-kind/ehr-write-connection-kind.component';
import { ToastService } from '../../../services/toast.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { configureFakeKind, fakeKindComponent, fakeRow } from '../../../connections/testing/fake-connection-kind';
import { ConnectionKindCard } from '../../../connections/connection-row.model';

/** The Destination Connections page only merges each kind's rows and dispatches to the kind that owns a row. */
describe('DestinationConnectionListComponent', () => {
  const FakeDest = fakeKindComponent('destination', 'app-destination-configuration-kind');
  const FakeWrite = fakeKindComponent('ehr-write', 'app-ehr-write-connection-kind');
  const MONGO = fakeRow('destination', 'd1', 'Docs', { raw: { destinationType: 'Mongo' } });
  const EPIC_WRITE = fakeRow('ehr-write', 'w1', 'Epic write');
  const CARD: ConnectionKindCard = { kind: 'ehr-write', value: 'Epic', label: 'Epic', sub: '', abbr: 'EP', color: 'x' };

  let toast: { success: jasmine.Spy; error: jasmine.Spy };

  function create() {
    toast = { success: jasmine.createSpy('success'), error: jasmine.createSpy('error') };
    TestBed.configureTestingModule({
      imports: [DestinationConnectionListComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ToastService, useValue: toast },
        { provide: PermissionService, useValue: { hasPermission: () => true } },
      ],
    });
    TestBed.overrideComponent(DestinationConnectionListComponent, {
      remove: { imports: [DestinationConfigurationKindComponent, EhrWriteConnectionKindComponent] },
      add: { imports: [FakeDest, FakeWrite] },
    });
    const fixture = TestBed.createComponent(DestinationConnectionListComponent);
    fixture.detectChanges();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      fixture, page: fixture.componentInstance, el,
      rowNames: () => Array.from(el.querySelectorAll('[data-testid="dest-row"]')).map(r => r.getAttribute('data-row-name')),
    };
  }

  it('merges destinations and EHR write connections into one list', () => {
    configureFakeKind('destination', [MONGO]);
    configureFakeKind('ehr-write', [EPIC_WRITE]);
    const { page, rowNames } = create();

    expect(rowNames()).toEqual(['Docs', 'Epic write']);
    expect(page.result().total).toBe(2);
  });

  it('filters by kind', () => {
    configureFakeKind('destination', [MONGO]);
    configureFakeKind('ehr-write', [EPIC_WRITE]);
    const { fixture, page, rowNames } = create();

    page.onFilters({ type: 'kind:ehr-write', status: '' });
    fixture.detectChanges();

    expect(rowNames()).toEqual(['Epic write']);
  });

  it('never asks a kind the role cannot list, and leaves its filter group out', () => {
    configureFakeKind('destination', [MONGO]);
    const write = configureFakeKind('ehr-write', [EPIC_WRITE], { canList: false });
    const { el, rowNames } = create();

    expect(write.listCalls).toBe(0);
    expect(rowNames()).toEqual(['Docs']);
    expect(el.querySelector('optgroup[label="EHR write connections"]')).toBeNull();
  });

  it('still shows the other kind when one fails, with that kind\'s toast', () => {
    configureFakeKind('destination', [], { list: () => throwError(() => new Error('down')) });
    configureFakeKind('ehr-write', [EPIC_WRITE]);
    const { rowNames } = create();

    expect(rowNames()).toEqual(['Epic write']);
    expect(toast.error).toHaveBeenCalledWith('destination failed');
  });

  it('hides New when no kind offers a card, and hands a picked card to its kind', () => {
    configureFakeKind('destination', [MONGO]);
    configureFakeKind('ehr-write', [EPIC_WRITE]);
    expect(create().el.querySelector('[data-testid="dest-add"]')).toBeNull();

    TestBed.resetTestingModule();
    configureFakeKind('destination', [MONGO]);
    const write = configureFakeKind('ehr-write', [EPIC_WRITE], { cards: [CARD] });
    const { fixture, el } = create();
    (el.querySelector('[data-testid="dest-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (el.querySelector('[data-testid="dest-vendor-card"][data-vendor="Epic"]') as HTMLButtonElement).click();

    expect(write.created).toEqual([CARD]);
  });

  it('sends a row action to the kind that owns the row', () => {
    const dest = configureFakeKind('destination', [MONGO]);
    const write = configureFakeKind('ehr-write', [EPIC_WRITE]);
    const { page } = create();

    page.onAction({ row: EPIC_WRITE, id: 'delete' });

    expect(write.ran).toEqual([{ id: 'delete', row: EPIC_WRITE }]);
    expect(dest.ran).toEqual([]);
  });

  it('reloads only the kind that changed', () => {
    const dest = configureFakeKind('destination', [MONGO]);
    const write = configureFakeKind('ehr-write', [EPIC_WRITE]);
    const { fixture } = create();

    fixture.debugElement.query(By.css('app-ehr-write-connection-kind')).componentInstance.changed.emit();

    expect(write.listCalls).toBe(2);
    expect(dest.listCalls).toBe(1);
  });

  it('tells the destination kind which of its rows are on screen', () => {
    const dest = configureFakeKind('destination', [MONGO]);
    configureFakeKind('ehr-write', [EPIC_WRITE]);
    create();

    expect(dest.shown.at(-1)).toEqual([MONGO]);
  });

  it('offers a filter option for a type present in the list but not in the catalog groups', () => {
    configureFakeKind('destination', [fakeRow('destination', 'd9', 'Legacy', { raw: { destinationType: 'Sftp' } })]);
    configureFakeKind('ehr-write', []);
    const { el } = create();

    expect(el.querySelector('optgroup[label="Other types"] option[value="destination:Sftp"]')).not.toBeNull();
  });

  it('pages the merged list across kinds and steps back when the last row of the last page goes', () => {
    const named = (kind: 'destination' | 'ehr-write', numbers: number[]) =>
      numbers.map(n => fakeRow(kind, `${kind}-${n}`, `Conn ${String(n).padStart(2, '0')}`, { raw: { destinationType: 'Mongo' } }));
    configureFakeKind('destination', named('destination', [1, 2, 3, 4, 5, 6, 12]));
    configureFakeKind('ehr-write', named('ehr-write', [7, 8, 9, 10, 11]));
    const { fixture, page, rowNames } = create();

    page.onPage({ pageIndex: 1, pageSize: 10 });
    fixture.detectChanges();

    expect(page.pageSize()).toBe(10);
    expect(page.result().total).toBe(12);
    expect(rowNames()).toEqual(['Conn 11', 'Conn 12']);
    expect(page.result().items.map(r => r.kind)).toEqual(['ehr-write', 'destination']);

    configureFakeKind('ehr-write', named('ehr-write', [7, 8, 9]));
    fixture.debugElement.query(By.css('app-ehr-write-connection-kind')).componentInstance.changed.emit();
    fixture.detectChanges();
    expect(page.result().pageIndex).toBe(0);
    expect(page.result().total).toBe(10);

    page.onAction({ row: page.result().items[0], id: 'view' });
    expect(page.pageIndex()).toBe(0);
  });

  it('searches in the browser and resets to the first page', () => {
    configureFakeKind('destination', [MONGO]);
    configureFakeKind('ehr-write', [EPIC_WRITE]);
    const { fixture, page, rowNames } = create();
    page.pageIndex.set(3);

    page.onSearch('epic');
    fixture.detectChanges();

    expect(page.pageIndex()).toBe(0);
    expect(rowNames()).toEqual(['Epic write']);
  });
});
