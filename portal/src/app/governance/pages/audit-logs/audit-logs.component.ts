import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialogModule } from '@angular/material/dialog';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AuditLogEntry, AuditLogFilter, AuditLogFilterOptions, PagedResult } from '../../models/governance.model';
import { LocalDateTimePipe } from '../../../core/pipes/local-date-time.pipe';
import { DiffDetailDialogComponent, DiffDetailDialogData, FieldDiff } from '../../dialogs/diff-detail-dialog/diff-detail-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { PageHelpComponent } from '../../../components/shared/page-help/page-help.component';

function formatValue(value: unknown): string {
  return value === undefined || value === null ? '—' : String(value);
}

type SortColumn = 'occurredOnUtc' | 'actor' | 'module' | 'action' | 'entity' | 'status' | 'correlationId';
type SortDirection = 'asc' | 'desc';

/** 'custom' uses the From/To date inputs; the rest count back from now. There is no "any time": the trail is never
 *  purged, so an unbounded default would load its whole history. */
type Period = '24h' | '7d' | '30d' | 'custom';

/** What the page opens with, and what Reset returns to. */
export const DEFAULT_PERIOD: Period = '24h';

const PERIOD_HOURS: Record<'24h' | '7d' | '30d', number> = { '24h': 24, '7d': 24 * 7, '30d': 24 * 30 };

/** What the filter bar holds. Edited freely; only copied into the applied set when Search is pressed. */
interface FilterDraft {
  correlationId: string;
  module: string;
  actor: string;
  action: string;
  period: Period;
  fromDate: string;
  toDate: string;
}

/** The filters a list was loaded with, plus the moment they were applied. A preset period counts back from that
 *  moment, not from each load, so paging and sorting stay on the same time window. */
interface AppliedFilters extends FilterDraft {
  appliedAtMs: number;
}

const EMPTY_DRAFT: FilterDraft = {
  correlationId: '', module: '', actor: '', action: '', period: DEFAULT_PERIOD, fromDate: '', toDate: '',
};

/** The audit trail only ever writes these three actions. */
export const AUDIT_ACTIONS = ['Created', 'Updated', 'Deleted'] as const;

@Component({
  selector: 'app-audit-logs',
  standalone: true,
  imports: [CommonModule, LocalDateTimePipe, MatTableModule, MatIconModule, MatTooltipModule, MatDialogModule, PaginationBarComponent, PageHelpComponent],
  templateUrl: './audit-logs.component.html',
  styleUrl: './audit-logs.component.scss',
})
export class AuditLogsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly customDialog = inject(DialogService);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entityType = signal('');
  readonly entityId = signal('');
  readonly result = signal<PagedResult<AuditLogEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  readonly sortColumn = signal<SortColumn>('occurredOnUtc');
  readonly sortDirection = signal<SortDirection>('desc');

  readonly module = signal('');
  readonly actor = signal('');
  readonly action = signal('');
  readonly period = signal<Period>(DEFAULT_PERIOD);
  /** yyyy-MM-dd from the date inputs, read as local days. */
  readonly fromDate = signal('');
  readonly toDate = signal('');
  readonly filterOptions = signal<AuditLogFilterOptions>({ modules: [], actors: [] });
  readonly actions = AUDIT_ACTIONS;

  /** The filters the current list was loaded with. Paging and sorting keep using these until Search is pressed. */
  private readonly applied = signal<AppliedFilters>({ ...EMPTY_DRAFT, appliedAtMs: Date.now() });

  /** Shown instead of searching when the filter bar cannot be applied as it stands. */
  readonly filterError = signal('');

  /** True when the filter bar differs from what the list is showing, i.e. Search has not been pressed yet. */
  readonly dirty = computed(() => {
    const a = this.applied();
    return this.correlationId().trim() !== a.correlationId || this.module() !== a.module || this.actor() !== a.actor
      || this.action() !== a.action || this.period() !== a.period || this.fromDate() !== a.fromDate
      || this.toDate() !== a.toDate;
  });

  /** Only meaningful once scoped to one entity via viewEntityHistory() — result().items is newest-first
   *  (SequenceNumber descending), so the oldest entry is v1. */
  readonly historyMode = computed(() => !!this.entityType() && !!this.entityId());

  readonly versionByEntryId = computed(() => {
    const oldestFirst = [...this.result().items].reverse();
    const map = new Map<string, number>();
    oldestFirst.forEach((entry, index) => map.set(entry.id, index + 1));
    return map;
  });

  readonly displayedCols = computed(() =>
    this.historyMode()
      ? ['version', 'occurredOnUtc', 'actor', 'module', 'action', 'entity', 'status', 'correlationId', 'actions']
      : ['occurredOnUtc', 'actor', 'module', 'action', 'entity', 'status', 'correlationId', 'actions']
  );

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    this.correlationId.set(params.get('correlationId') ?? '');
    this.entityType.set(params.get('entityType') ?? '');
    this.entityId.set(params.get('entityId') ?? '');
    this.applied.set({ ...this.draft(), appliedAtMs: Date.now() });
    this.api.auditLogFilterOptions().subscribe({
      next: options => this.filterOptions.set(options),
      // The list still works without the dropdown values; the filters just offer nothing to pick.
      error: () => this.filterOptions.set({ modules: [], actors: [] }),
    });
    this.load();
  }

  load(): void {
    this.loading.set(true);
    // historyMode never sends a sort override — versionByEntryId assumes the backend's default order
    // (newest-first by the hash-chain's own sequence number) to number versions oldest-first correctly.
    const applied = this.applied();
    this.api.auditLogs(
      applied.correlationId || undefined,
      this.pageIndex() + 1,
      this.pageSize(),
      this.entityType() || undefined,
      this.entityId() || undefined,
      this.historyMode() ? undefined : this.sortColumn(),
      this.historyMode() ? undefined : this.sortDirection(),
      this.historyMode() ? undefined : this.toRequestFilter(applied),
    ).subscribe({
      next: result => { this.result.set(result); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  onSort(column: SortColumn): void {
    if (this.historyMode()) return;
    if (this.sortColumn() === column) {
      this.sortDirection.update(d => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
    this.pageIndex.set(0);
    this.load();
  }

  onCorrelationIdChange(value: string): void {
    this.correlationId.set(value);
  }

  /** Filter controls only edit the filter bar; Search applies them (together with the correlation id). */
  onFilterChange(key: 'module' | 'actor' | 'action' | 'fromDate' | 'toDate', value: string): void {
    this[key].set(value);
    this.filterError.set('');
  }

  onPeriodChange(value: Period): void {
    this.period.set(value);
    this.filterError.set('');
    if (value !== 'custom') {
      this.fromDate.set('');
      this.toDate.set('');
    }
  }

  /** Applies everything in the filter bar and goes back to the first page. */
  search(): void {
    // A custom range with no start would load the trail's whole history.
    if (this.period() === 'custom' && !this.fromDate()) {
      this.filterError.set('Choose a From date for the custom range.');
      return;
    }
    this.filterError.set('');
    this.applied.set({ ...this.draft(), appliedAtMs: Date.now() });
    this.pageIndex.set(0);
    this.load();
  }

  reset(): void {
    this.entityType.set('');
    this.entityId.set('');
    this.clearFilters();
    this.pageIndex.set(0);
    this.load();
  }

  /** Empties the filter bar and the applied set alike. */
  private clearFilters(): void {
    this.correlationId.set('');
    this.module.set('');
    this.actor.set('');
    this.action.set('');
    this.period.set(DEFAULT_PERIOD);
    this.fromDate.set('');
    this.toDate.set('');
    this.filterError.set('');
    this.applied.set({ ...EMPTY_DRAFT, appliedAtMs: Date.now() });
  }

  private draft(): FilterDraft {
    return {
      correlationId: this.correlationId().trim(),
      module: this.module(),
      actor: this.actor(),
      action: this.action(),
      period: this.period(),
      fromDate: this.fromDate(),
      toDate: this.toDate(),
    };
  }

  /** Builds the request filter. Custom dates are whole local days: From at its start, To up to the end of that day.
   *  A preset period counts back from the moment the filters were applied. */
  private toRequestFilter(applied: AppliedFilters): AuditLogFilter {
    const filter: AuditLogFilter = {
      module: applied.module || undefined,
      actor: applied.actor || undefined,
      action: applied.action || undefined,
    };

    if (applied.period === 'custom') {
      if (applied.fromDate) {
        filter.fromUtc = this.localDayStart(applied.fromDate).toISOString();
      }
      if (applied.toDate) {
        const end = this.localDayStart(applied.toDate);
        end.setDate(end.getDate() + 1);
        filter.toUtc = end.toISOString();
      }
    } else {
      filter.fromUtc = new Date(applied.appliedAtMs - PERIOD_HOURS[applied.period] * 60 * 60 * 1000).toISOString();
    }

    return filter;
  }

  private localDayStart(isoDate: string): Date {
    const [year, month, day] = isoDate.split('-').map(Number);
    return new Date(year, month - 1, day);
  }

  /** Scopes the list down to just this row's entity — its full version history. */
  viewEntityHistory(entry: AuditLogEntry): void {
    if (!entry.entityType || !entry.entityId) {
      return;
    }
    // Version history is the entity's whole trail; a leftover filter would hide versions and misnumber the rest.
    this.clearFilters();
    this.entityType.set(entry.entityType);
    this.entityId.set(entry.entityId);
    this.pageIndex.set(0);
    this.load();
  }

  openDiff(entry: AuditLogEntry): void {
    this.customDialog.open<DiffDetailDialogComponent, DiffDetailDialogData, void>(DiffDetailDialogComponent, {
      data: { module: entry.module, action: entry.action, diffs: this.diffFields(entry) },
    });
  }

  version(entry: AuditLogEntry): number | null {
    return this.versionByEntryId().get(entry.id) ?? null;
  }

  compareVersions(): void {
    if (!this.entityType() || !this.entityId()) {
      return;
    }
    this.router.navigate(['/governance/configuration-comparison'], {
      queryParams: { entityType: this.entityType(), entityId: this.entityId() },
    });
  }

  /** A version's diff is already embedded in the row — Old/New JSON of that one change — so this is a
   *  pure client-side computation, not a separate "configuration comparison" data path. */
  diffFields(entry: AuditLogEntry): FieldDiff[] {
    const oldObj = this.tryParse(entry.oldValueJson);
    const newObj = this.tryParse(entry.newValueJson);
    const keys = new Set([...Object.keys(oldObj), ...Object.keys(newObj)]);
    const diffs: FieldDiff[] = [];

    for (const key of keys) {
      const oldVal = oldObj[key];
      const newVal = newObj[key];
      if (oldVal === newVal) {
        continue;
      }

      const changeType: FieldDiff['changeType'] =
        oldVal === undefined ? 'added' : newVal === undefined ? 'removed' : 'changed';

      diffs.push({ field: key, oldValue: formatValue(oldVal), newValue: formatValue(newVal), changeType });
    }

    return diffs.sort((a, b) => a.field.localeCompare(b.field));
  }

  private tryParse(json: string | null): Record<string, unknown> {
    if (!json) {
      return {};
    }
    try {
      return JSON.parse(json);
    } catch {
      return {};
    }
  }
}
