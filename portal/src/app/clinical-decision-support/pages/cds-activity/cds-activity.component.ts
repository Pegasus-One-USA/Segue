import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { MatTableModule } from '@angular/material/table';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Subscription, catchError, of } from 'rxjs';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsCallKind, CdsCallLog, CdsCallOutcome, CdsServiceInstance } from '../../models/cds.models';
import { CDS_CALL_OUTCOMES, cdsHookLabel, cdsOutcomeLabel, cdsOutcomeTone } from '../../models/cds-labels';
import { LocalDateTimePipe } from '../../../core/pipes/local-date-time.pipe';
import { PageChangeEvent, PaginationBarComponent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { ToastService } from '../../../services/toast.service';
import { CdsCallExtrasComponent } from '../../components/cds-call-extras/cds-call-extras.component';
import { CdsFeedbackSummaryComponent } from '../../components/cds-feedback-summary/cds-feedback-summary.component';
import { CdsCallCoverageComponent } from '../../components/cds-call-coverage/cds-call-coverage.component';

/** Every call EHRs made to the CDS services, newest first. The log holds no patient data, so none is shown. */
@Component({
  selector: 'app-cds-activity',
  standalone: true,
  imports: [
    MatTableModule,
    MatIconModule,
    MatTooltipModule,
    LocalDateTimePipe,
    PaginationBarComponent,
    CdsCallExtrasComponent,
    CdsFeedbackSummaryComponent,
    CdsCallCoverageComponent,
  ],
  templateUrl: './cds-activity.component.html',
  styleUrl: './cds-activity.component.scss',
})
export class CdsActivityComponent implements OnInit, OnDestroy {
  private readonly api   = inject(CdsApiService);
  private readonly toast = inject(ToastService);

  readonly loading    = signal(true);
  readonly items      = signal<CdsCallLog[]>([]);
  readonly totalCount = signal(0);
  readonly pageIndex  = signal(0);
  readonly pageSize   = signal(25);
  readonly services   = signal<CdsServiceInstance[]>([]);

  readonly serviceFilter = signal('');
  readonly kindFilter    = signal<CdsCallKind | ''>('');
  readonly outcomeFilter = signal<CdsCallOutcome | ''>('');
  readonly fromDate      = signal('');
  readonly toDate        = signal('');

  readonly outcomes = CDS_CALL_OUTCOMES;
  readonly outcomeLabel = cdsOutcomeLabel;
  readonly outcomeTone = cdsOutcomeTone;
  readonly hookLabel = cdsHookLabel;

  /** The in-flight activity request, cancelled when a newer one starts. */
  private pending?: Subscription;

  readonly displayedCols = ['occurred', 'kind', 'service', 'hook', 'issuer', 'outcome', 'cards', 'duration', 'detail'];

  readonly hasActiveFilter = computed(() =>
    !!this.serviceFilter() || !!this.kindFilter() || !!this.outcomeFilter() || !!this.fromDate() || !!this.toDate());

  ngOnInit(): void {
    this.api.getServices().pipe(catchError(() => of([] as CdsServiceInstance[])))
      .subscribe(services => this.services.set([...services].sort((a, b) => a.serviceId.localeCompare(b.serviceId))));
    this.load();
  }

  ngOnDestroy(): void {
    this.pending?.unsubscribe();
  }

  load(): void {
    this.loading.set(true);
    // Only the latest filters' answer may land: a slower, older response must not overwrite it.
    this.pending?.unsubscribe();
    this.pending = this.api.getActivity({
      serviceId: this.serviceFilter() || undefined,
      kind: this.kindFilter() || undefined,
      outcome: this.outcomeFilter() || undefined,
      fromUtc: this.startOfDayUtc(this.fromDate()),
      toUtc: this.endOfDayUtc(this.toDate()),
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }).subscribe({
      next: page => {
        this.items.set(page.items);
        this.totalCount.set(page.totalCount);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Could not load CDS activity.');
      },
    });
  }

  onFilterChange(apply: () => void): void {
    apply();
    this.pageIndex.set(0);
    this.load();
  }

  setService(value: string): void { this.onFilterChange(() => this.serviceFilter.set(value)); }
  setKind(value: string): void { this.onFilterChange(() => this.kindFilter.set(value as CdsCallKind | '')); }
  setOutcome(value: string): void { this.onFilterChange(() => this.outcomeFilter.set(value as CdsCallOutcome | '')); }
  setFrom(value: string): void { this.onFilterChange(() => this.fromDate.set(value)); }
  setTo(value: string): void { this.onFilterChange(() => this.toDate.set(value)); }

  reset(): void {
    this.serviceFilter.set('');
    this.kindFilter.set('');
    this.outcomeFilter.set('');
    this.fromDate.set('');
    this.toDate.set('');
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(event: PageChangeEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.load();
  }

  kindLabel(kind: CdsCallKind): string {
    return kind === 'Feedback' ? 'Feedback' : 'Call';
  }

  /** A yyyy-mm-dd date input, read as local midnight, as an ISO UTC instant. */
  private startOfDayUtc(date: string): string | undefined {
    if (!date) return undefined;
    const [y, m, d] = date.split('-').map(Number);
    return new Date(y, m - 1, d, 0, 0, 0, 0).toISOString();
  }

  private endOfDayUtc(date: string): string | undefined {
    if (!date) return undefined;
    const [y, m, d] = date.split('-').map(Number);
    return new Date(y, m - 1, d, 23, 59, 59, 999).toISOString();
  }
}
