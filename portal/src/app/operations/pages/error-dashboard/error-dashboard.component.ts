import { Component, ElementRef, EventEmitter, Input, OnInit, Output, ViewChild, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { SettingsPageDialogService } from '../../../settings/services/settings-page-dialog.service';
import { ToastService } from '../../../services/toast.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { OperationsApiService } from '../../services/operations-api.service';
import { ErrorCount, ErrorDashboard, ErrorLogEntry, ErrorLogStorage, PagedResult } from '../../models/operations.model';
import { ErrorDetailDialogComponent, ErrorListTarget } from '../../components/error-detail-dialog/error-detail-dialog.component';

interface RangeOption { label: string; days: number; }

/** The filters the page is actually showing (as opposed to what is being picked in the filter bar). */
interface AppliedFilters {
  rangeDays: number;
  customFrom: string;
  customTo: string;
  severity: string;
  category: string;
  /** yyyy-MM-dd (UTC) when the user clicked one day's bar in the chart; replaces the period. */
  day: string | null;
}

interface FilterChip { key: 'period' | 'severity' | 'category' | 'day'; label: string; value: string; removable: boolean; }

@Component({
  selector: 'app-error-dashboard',
  standalone: true,
  imports: [CommonModule, DatePipe, MatPaginatorModule, ErrorDetailDialogComponent],
  templateUrl: './error-dashboard.component.html',
  styleUrl: './error-dashboard.component.scss',
})
export class ErrorDashboardComponent implements OnInit {
  /** True when hosted inside the Errors page, which supplies the page title and the tabs. */
  @Input() embedded = false;

  /** The user wants the matching errors in the All errors list. */
  @Output() readonly viewInList = new EventEmitter<ErrorListTarget>();

  private readonly api = inject(OperationsApiService);
  private readonly toast = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly settingsDialog = inject(SettingsPageDialogService);

  readonly ranges: RangeOption[] = [
    { label: 'Last 24 hours', days: 1 },
    { label: 'Last 7 days', days: 7 },
    { label: 'Last 14 days', days: 14 },
    { label: 'Last 30 days', days: 30 },
    { label: 'Last 90 days', days: 90 },
    { label: 'Custom range…', days: 0 },
  ];
  readonly severityOptions = ['Critical', 'Error', 'Warning', 'Information', 'WorkflowDebug'];
  readonly categoryOptions = ['Unknown', 'Business', 'Validation', 'Infrastructure', 'Authentication', 'Authorization', 'Database', 'Network', 'ExternalSystem'];

  // ── Filter bar (draft): nothing happens until "Apply filters" ───────────────────────────────
  /** 0 = custom date range (customFrom / customTo). */
  readonly rangeDays = signal(14);
  readonly customFrom = signal(this.dateInput(-14));
  readonly customTo = signal(this.dateInput(0));
  readonly severity = signal('');
  readonly category = signal('');

  // ── What is currently applied ───────────────────────────────────────────────────────────────
  readonly applied = signal<AppliedFilters>({
    rangeDays: 14, customFrom: this.dateInput(-14), customTo: this.dateInput(0), severity: '', category: '', day: null,
  });

  readonly storage = signal<ErrorLogStorage | null>(null);
  readonly includeStackTrace = signal(true);
  readonly includeCorrelation = signal(true);
  readonly loading = signal(false);
  readonly exporting = signal(false);
  readonly data = signal<ErrorDashboard | null>(null);

  // ── Matching errors (the list under the charts) ─────────────────────────────────────────────
  readonly matches = signal<PagedResult<ErrorLogEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 10 });
  readonly matchesLoading = signal(false);
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  // ── Permanent delete dialog ─────────────────────────────────────────────────────────────────
  @ViewChild(ErrorDetailDialogComponent) private detailDialog?: ErrorDetailDialogComponent;
  @ViewChild('deleteDialog') private deleteDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('hideDialog') private hideDialog?: ElementRef<HTMLDialogElement>;
  readonly hideBefore = signal(this.dateInput(0));
  readonly hiding = signal(false);
  /** Latest date the "hide" picker allows (today). */
  readonly todayInput = this.dateInput(0);
  readonly deleteMode = signal<'all' | 'olderThan'>('olderThan');
  readonly deleteBefore = signal(this.dateInput(-30));
  readonly deleteConfirm = signal('');
  readonly deleting = signal(false);

  readonly maxDay = computed(() => Math.max(1, ...(this.data()?.byDay ?? []).map(d => d.count)));

  /** The picked-but-not-applied filters differ from what is on screen. */
  readonly dirty = computed(() => {
    const a = this.applied();
    return a.rangeDays !== this.rangeDays()
      || (this.rangeDays() === 0 && (a.customFrom !== this.customFrom() || a.customTo !== this.customTo()))
      || a.severity !== this.severity()
      || a.category !== this.category();
  });

  /** Plain-language description of the criteria in force, shown as chips above the results. */
  readonly chips = computed<FilterChip[]>(() => {
    const a = this.applied();
    const chips: FilterChip[] = [];
    if (a.day) {
      chips.push({ key: 'day', label: 'Day', value: `${this.formatDay(a.day)} (UTC)`, removable: true });
    } else {
      const label = a.rangeDays === 0
        ? `${a.customFrom} to ${a.customTo}`
        : (this.ranges.find(r => r.days === a.rangeDays)?.label ?? `Last ${a.rangeDays} days`);
      chips.push({ key: 'period', label: 'Period', value: label, removable: false });
    }

    if (a.severity) { chips.push({ key: 'severity', label: 'Type', value: a.severity, removable: true }); }
    if (a.category) { chips.push({ key: 'category', label: 'Category', value: a.category, removable: true }); }
    return chips;
  });

  readonly canDelete = computed(() =>
    this.deleteConfirm().trim().toUpperCase() === 'DELETE' && (this.deleteMode() === 'all' || !!this.deleteBefore()));

  ngOnInit(): void {
    this.load();
    this.refreshStorage();
  }

  private refreshStorage(): void {
    this.api.errorLogStorage().subscribe({ next: s => this.storage.set(s), error: () => this.storage.set(null) });
  }

  openErrorLogSettings(): void {
    this.settingsDialog.open('error-log');
  }

  formatBytes(bytes: number | null | undefined): string {
    if (bytes === null || bytes === undefined) { return 'size not available'; }
    if (bytes < 1024) { return `${bytes} B`; }
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = bytes / 1024;
    let i = 0;
    while (value >= 1024 && i < units.length - 1) { value /= 1024; i++; }
    return `${value.toFixed(value < 10 ? 2 : 1)} ${units[i]}`;
  }

  private formatDay(day: string): string {
    return new Date(`${day}T00:00:00Z`).toLocaleDateString(undefined, { timeZone: 'UTC', year: 'numeric', month: 'short', day: 'numeric' });
  }

  /** yyyy-MM-dd for an <input type="date">, offset by whole days from today. */
  private dateInput(offsetDays: number): string {
    const d = new Date();
    d.setDate(d.getDate() + offsetDays);
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  /** The window the page is showing: one UTC day when a bar was clicked, otherwise the applied period. */
  private range(): { fromUtc: string; toUtc: string } {
    const a = this.applied();
    if (a.day) {
      const from = new Date(`${a.day}T00:00:00Z`);
      return { fromUtc: from.toISOString(), toUtc: new Date(from.getTime() + 24 * 60 * 60 * 1000 - 1).toISOString() };
    }

    if (a.rangeDays === 0) {
      // Custom range: local start of the first day through the end of the last day.
      const from = new Date(`${a.customFrom}T00:00:00`);
      const to = new Date(`${a.customTo}T23:59:59.999`);
      if (!isNaN(from.getTime()) && !isNaN(to.getTime())) {
        return from <= to
          ? { fromUtc: from.toISOString(), toUtc: to.toISOString() }
          : { fromUtc: to.toISOString(), toUtc: from.toISOString() };
      }
    }

    const to = new Date();
    const days = a.rangeDays || 14;
    const from = new Date(to.getTime() - days * 24 * 60 * 60 * 1000);
    return { fromUtc: from.toISOString(), toUtc: to.toISOString() };
  }

  // ── Filter bar actions ───────────────────────────────────────────────────────────────────────
  setRange(days: number): void { this.rangeDays.set(days); }

  setCustomDate(which: 'from' | 'to', value: string): void {
    (which === 'from' ? this.customFrom : this.customTo).set(value);
  }

  /** Makes the picked filters the ones in force and shows the results. */
  applyFilters(): void {
    this.applied.set({
      rangeDays: this.rangeDays(),
      customFrom: this.customFrom(),
      customTo: this.customTo(),
      severity: this.severity(),
      category: this.category(),
      day: null,
    });
    this.pageIndex.set(0);
    this.load();
  }

  resetFilters(): void {
    this.rangeDays.set(14);
    this.customFrom.set(this.dateInput(-14));
    this.customTo.set(this.dateInput(0));
    this.severity.set('');
    this.category.set('');
    this.applyFilters();
  }

  /** Clicking a bar in the chart narrows everything on the page to that one day; clicking it again undoes that. */
  selectDay(dayUtc: string): void {
    const day = dayUtc.slice(0, 10);
    this.applied.update(a => ({ ...a, day: a.day === day ? null : day }));
    this.pageIndex.set(0);
    this.load();
  }

  removeChip(chip: FilterChip): void {
    if (chip.key === 'day') {
      this.applied.update(a => ({ ...a, day: null }));
    } else if (chip.key === 'severity') {
      this.severity.set('');
      this.applied.update(a => ({ ...a, severity: '' }));
    } else if (chip.key === 'category') {
      this.category.set('');
      this.applied.update(a => ({ ...a, category: '' }));
    }

    this.pageIndex.set(0);
    this.load();
  }

  isSelectedDay(dayUtc: string): boolean {
    return this.applied().day === dayUtc.slice(0, 10);
  }

  load(): void {
    this.loading.set(true);
    const { fromUtc, toUtc } = this.range();
    const a = this.applied();
    this.api.errorDashboard(fromUtc, toUtc, a.severity || undefined, a.category || undefined).subscribe({
      next: result => { this.data.set(result); this.loading.set(false); this.loadMatches(); },
      error: () => this.loading.set(false),
    });
  }

  /** The errors behind the numbers above: same period and filters, newest first. */
  loadMatches(): void {
    this.matchesLoading.set(true);
    const window = this.range();
    let fromUtc = window.fromUtc;
    // "Hide old errors" also applies to this list, so it always agrees with the counts above it.
    const hiddenBefore = this.data()?.clearedAtUtc;
    if (hiddenBefore && new Date(hiddenBefore) > new Date(fromUtc)) { fromUtc = hiddenBefore; }
    const a = this.applied();
    this.api.searchErrors({
      fromUtc,
      toUtc: window.toUtc,
      severity: a.severity || undefined,
      category: a.category || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }).subscribe({
      next: result => { this.matches.set(result); this.matchesLoading.set(false); },
      error: () => this.matchesLoading.set(false),
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadMatches();
  }

  exportReport(format: 'json' | 'csv'): void {
    this.exporting.set(true);
    const { fromUtc, toUtc } = this.range();
    const a = this.applied();
    this.api.exportErrorReport(format, fromUtc, toUtc, this.includeStackTrace(), {
      severity: a.severity,
      category: a.category,
    }, this.includeCorrelation()).subscribe({
      next: blob => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `fhirbridge-error-report-${new Date().toISOString().slice(0, 10)}.${format}`;
        link.click();
        URL.revokeObjectURL(url);
        this.exporting.set(false);
        this.toast.success('Error report downloaded. Please review it before sharing.');
      },
      error: () => this.exporting.set(false),
    });
  }

  /** Opens the shared error popup, either for a row in hand or for a reference id. */
  openDetail(source: string | ErrorLogEntry | null): void {
    this.detailDialog?.open(source);
  }

  // ── Hide old errors (reversible) ─────────────────────────────────────────────────────────────
  /** Opens the dialog that asks which errors to hide. Nothing is deleted. */
  confirmHide(): void {
    if (!this.actionGuard.ensure('governance.write', 'You do not have permission to hide errors.')) { return; }
    this.hideBefore.set(this.dateInput(0));
    this.hideDialog?.nativeElement.showModal();
  }

  closeHide(): void {
    this.hideDialog?.nativeElement.close();
  }

  hideErrors(): void {
    if (!this.hideBefore()) { return; }
    this.hiding.set(true);
    // Start of the chosen day in the user's own time zone: everything recorded before it is hidden.
    const beforeUtc = new Date(`${this.hideBefore()}T00:00:00`).toISOString();
    this.api.clearErrorDashboard(beforeUtc).subscribe({
      next: () => {
        this.hiding.set(false);
        this.closeHide();
        this.toast.success(`Errors recorded before ${this.hideBefore()} are now hidden from the Overview.`);
        this.load();
      },
      error: () => this.hiding.set(false),
    });
  }

  restoreHidden(): void {
    if (!this.actionGuard.ensure('governance.write', 'You do not have permission to change the Overview.')) { return; }
    this.api.restoreErrorDashboard().subscribe({
      next: () => { this.toast.success('Hidden errors are shown again.'); this.load(); },
    });
  }

  // ── Delete errors permanently (frees space) ──────────────────────────────────────────────────
  openDelete(): void {
    if (!this.actionGuard.ensure('governance.delete', 'You do not have permission to delete errors.')) { return; }
    this.deleteMode.set('olderThan');
    this.deleteBefore.set(this.dateInput(-30));
    this.deleteConfirm.set('');
    this.deleteDialog?.nativeElement.showModal();
  }

  closeDelete(): void {
    this.deleteDialog?.nativeElement.close();
  }

  deletePermanently(): void {
    if (!this.canDelete()) { return; }
    this.deleting.set(true);
    const olderThanUtc = this.deleteMode() === 'olderThan' ? new Date(`${this.deleteBefore()}T00:00:00`).toISOString() : null;
    this.api.deleteErrors(this.deleteMode(), olderThanUtc).subscribe({
      next: result => {
        this.deleting.set(false);
        this.closeDelete();
        const freed = (result.spaceReclaimed ? ' The space was released.' : '') + (result.note ? ` ${result.note}` : '');
        this.toast.success(`${result.deleted} error${result.deleted === 1 ? '' : 's'} permanently deleted.${freed}`);
        this.load();
        this.refreshStorage();
      },
      error: () => this.deleting.set(false),
    });
  }

  percent(item: ErrorCount, list: ErrorCount[]): number {
    const max = Math.max(1, ...list.map(x => x.count));
    return Math.round((item.count / max) * 100);
  }

  barHeight(count: number): number {
    return Math.max(2, Math.round((count / this.maxDay()) * 100));
  }
}
