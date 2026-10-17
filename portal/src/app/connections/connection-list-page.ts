import { AfterViewInit, Directive, WritableSignal, computed, inject, signal, viewChildren } from '@angular/core';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ToastService } from '../services/toast.service';
import { PageChangeEvent } from '../components/shared/pagination-bar/pagination-bar.component';
import {
  CONNECTION_KIND_HOST,
  ConnectionActionId,
  ConnectionKind,
  ConnectionKindCard,
  ConnectionKindHost,
  ConnectionRow,
  ConnectionRowAction,
  ConnectionSort,
  ConnectionSortKey,
} from './connection-row.model';
import { ConnectionListQuery, queryConnectionRows } from './connection-list-query';

/** What every connection list's filter value carries besides its own choices: the status select. */
export interface ConnectionListFilterValue {
  status: ConnectionListQuery['status'];
}

/**
 * The behaviour Source Connections and Destination Connections share: one merged list of rows from each kind of
 * connection the page hosts, searched, filtered, sorted and paged in the browser, with row actions and the "New" picker
 * dispatched to the kind that owns a row or card. Each kind is its own component providing CONNECTION_KIND_HOST; the
 * page finds them with viewChildren (they must sit outside any @if so they exist before the first load) and only
 * supplies its columns, its filters (and how they become row filter keys) and its picker headings.
 */
@Directive()
export abstract class ConnectionListPage<F extends ConnectionListFilterValue> implements AfterViewInit {
  private readonly toast = inject(ToastService);

  /** The kinds this page lists, found by the token they provide — dispatch is by `kind`, never a switch. */
  readonly hosts = viewChildren(CONNECTION_KIND_HOST);

  /** Rows per kind; a kind missing here has not loaded (or cannot be listed by this role). */
  readonly rowsByKind = signal<Partial<Record<ConnectionKind, ConnectionRow[]>>>({});
  readonly search = signal('');
  readonly sort = signal<ConnectionSort>({ key: 'name', dir: 'asc' });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  /** Starts true so the first load, run after the view is built, changes nothing the first check already saw. */
  readonly loading = signal(true);
  readonly pickerOpen = signal(false);

  /** The page's own filter choices; `emptyFilters` is what Reset restores. */
  abstract readonly filters: WritableSignal<F>;
  protected abstract readonly emptyFilters: F;
  /** The row filter keys the page's filter choices stand for (see connection-list-query.ts). */
  protected abstract filterKeys(filters: F): string[];

  readonly result = computed(() => queryConnectionRows(Object.values(this.rowsByKind()).flat(), {
    search: this.search(),
    filterKeys: this.filterKeys(this.filters()),
    status: this.filters().status,
    sort: this.sort(),
    pageIndex: this.pageIndex(),
    pageSize: this.pageSize(),
  }));

  readonly isFiltered = computed(() =>
    !!this.search().trim() || this.filterKeys(this.filters()).length > 0 || !!this.filters().status);

  readonly canCreate = computed(() => this.hosts().some(h => h.createCards().length > 0));

  readonly actionsFor = (row: ConnectionRow): ConnectionRowAction[] => this.hostOf(row.kind)?.rowActions(row) ?? [];

  ngAfterViewInit(): void {
    this.reload();
  }

  protected hostOf(kind: ConnectionKind): ConnectionKindHost | undefined {
    return this.hosts().find(h => h.kind === kind);
  }

  protected cardsOf(kind: ConnectionKind): ConnectionKindCard[] {
    return this.hostOf(kind)?.createCards() ?? [];
  }

  /** Every listable kind, or just the one that changed. One kind failing still shows the others. */
  reload(kind?: ConnectionKind): void {
    const hosts = this.hosts().filter(h => h.canList() && (!kind || h.kind === kind));
    if (hosts.length === 0) {
      this.loading.set(false);
      return;
    }
    forkJoin(hosts.map(host => host.list().pipe(catchError(() => {
      this.toast.error(host.loadErrorMessage);
      return of<ConnectionRow[]>([]);
    })))).subscribe(lists => {
      this.rowsByKind.update(current => {
        const next = { ...current };
        hosts.forEach((host, i) => (next[host.kind] = lists[i]));
        return next;
      });
      this.loading.set(false);
    });
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.pageIndex.set(0);
  }

  onFilters(value: F): void {
    this.filters.set(value);
    this.pageIndex.set(0);
  }

  /** Same column flips the direction; a new one starts ascending, except "Action on", newest first. */
  onSort(key: ConnectionSortKey): void {
    const current = this.sort();
    this.sort.set(current.key === key
      ? { key, dir: current.dir === 'asc' ? 'desc' : 'asc' }
      : { key, dir: key === 'actionOn' ? 'desc' : 'asc' });
    this.pageIndex.set(0);
  }

  onPage(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  reset(): void {
    this.search.set('');
    this.filters.set(this.emptyFilters);
    this.sort.set({ key: 'name', dir: 'asc' });
    this.pageIndex.set(0);
    this.reload();
  }

  /** Paging continues from the page actually shown (it may have been clamped after a kind's rows shrank). */
  onAction(event: { row: ConnectionRow; id: ConnectionActionId }): void {
    this.pageIndex.set(this.result().pageIndex);
    this.hostOf(event.row.kind)?.runAction(event.id, event.row);
  }

  onPicked(card: ConnectionKindCard): void {
    if (this.hostOf(card.kind)?.create(card)) this.pickerOpen.set(false);
  }
}
