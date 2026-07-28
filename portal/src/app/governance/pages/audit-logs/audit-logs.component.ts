import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AuditLogEntry, PagedResult } from '../../models/governance.model';
import { LocalDateTimePipe } from '../../../core/pipes/local-date-time.pipe';
import { DiffDetailDialogComponent, FieldDiff } from '../../dialogs/diff-detail-dialog/diff-detail-dialog.component';

function formatValue(value: unknown): string {
  return value === undefined || value === null ? '—' : String(value);
}

@Component({
  selector: 'app-audit-logs',
  standalone: true,
  imports: [CommonModule, LocalDateTimePipe, MatTableModule, MatPaginatorModule, MatIconModule, MatTooltipModule, MatDialogModule],
  templateUrl: './audit-logs.component.html',
  styleUrl: './audit-logs.component.scss',
})
export class AuditLogsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entityType = signal('');
  readonly entityId = signal('');
  readonly result = signal<PagedResult<AuditLogEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

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
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.auditLogs(
      this.correlationId() || undefined,
      this.pageIndex() + 1,
      this.pageSize(),
      this.entityType() || undefined,
      this.entityId() || undefined,
    ).subscribe({
      next: result => { this.result.set(result); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  onCorrelationIdChange(value: string): void {
    this.correlationId.set(value);
  }

  reset(): void {
    this.correlationId.set('');
    this.entityType.set('');
    this.entityId.set('');
    this.pageIndex.set(0);
    this.load();
  }

  /** Scopes the list down to just this row's entity — its full version history. */
  viewEntityHistory(entry: AuditLogEntry): void {
    if (!entry.entityType || !entry.entityId) {
      return;
    }
    this.correlationId.set('');
    this.entityType.set(entry.entityType);
    this.entityId.set(entry.entityId);
    this.pageIndex.set(0);
    this.load();
  }

  openDiff(entry: AuditLogEntry): void {
    this.dialog.open(DiffDetailDialogComponent, {
      data: { module: entry.module, action: entry.action, diffs: this.diffFields(entry) },
      autoFocus: false,
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
