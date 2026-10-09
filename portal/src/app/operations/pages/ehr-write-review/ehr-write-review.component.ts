import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { AuthStore } from '../../../auth/store/auth.store';
import { EhrWriteLedgerService, EhrWriteReviewItem, EhrWriteReviewPage } from '../../../services/ehr-write-ledger.service';

/**
 * EHR write-back review list. Writes whose outcome is unknown, that the EHR refused, or that never finished sending
 * are never retried on their own, because the EHR files a repeated create as a second copy. A person checks the
 * chart and either records the EHR's id ("it is there") or releases the record for one more send.
 */
@Component({
  selector: 'app-ehr-write-review',
  standalone: true,
  imports: [DatePipe, MatTableModule, MatPaginatorModule],
  templateUrl: './ehr-write-review.component.html',
  styleUrl: './ehr-write-review.component.scss',
})
export class EhrWriteReviewComponent implements OnInit {
  private readonly api = inject(EhrWriteLedgerService);
  private readonly store = inject(AuthStore);

  readonly resourceTypes = ['AllergyIntolerance', 'Condition', 'DocumentReference', 'Observation', 'Patient'];
  readonly displayedCols = ['updatedOnUtc', 'resourceType', 'target', 'state', 'response', 'run', 'actions'];

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly resourceType = signal<string | null>(null);
  readonly pageIndex = signal(0);
  readonly pageSize = signal(25);
  readonly result = signal<EhrWriteReviewPage>({ items: [], totalCount: 0, page: 1, pageSize: 25 });

  /** The row whose "it is in the EHR" box is open, and the id typed into it. */
  readonly markingId = signal<string | null>(null);
  readonly targetIdDraft = signal('');
  /** The row whose "send again" is waiting for a second click. */
  readonly releasingId = signal<string | null>(null);
  readonly busyId = signal<string | null>(null);

  readonly canResolve = computed(() => this.store.isAdmin() || this.store.hasPermission('ehrwriteback.edit'));

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.review(this.resourceType(), this.pageIndex() + 1, this.pageSize()).subscribe({
      next: page => { this.result.set(page); this.loading.set(false); },
      error: (e: HttpErrorResponse) => { this.error.set(this.messageOf(e)); this.loading.set(false); },
    });
  }

  onResourceTypeChange(value: string): void {
    this.resourceType.set(value || null);
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  startMarking(row: EhrWriteReviewItem): void {
    this.releasingId.set(null);
    this.markingId.set(row.id);
    this.targetIdDraft.set(row.targetResourceId ?? '');
  }

  cancel(): void {
    this.markingId.set(null);
    this.releasingId.set(null);
  }

  confirmWritten(row: EhrWriteReviewItem): void {
    const targetId = this.targetIdDraft().trim();
    if (!targetId) return;
    this.resolve(row, this.api.markWritten(row.id, targetId));
  }

  askRelease(row: EhrWriteReviewItem): void {
    this.markingId.set(null);
    this.releasingId.set(row.id);
  }

  confirmRelease(row: EhrWriteReviewItem): void {
    this.resolve(row, this.api.release(row.id));
  }

  stateLabel(state: string): string {
    switch (state) {
      case 'Unknown': return 'Outcome unknown';
      case 'Abandoned': return 'Send never finished';
      case 'Rejected': return 'Refused by the EHR';
      default: return state;
    }
  }

  private resolve(row: EhrWriteReviewItem, call: ReturnType<EhrWriteLedgerService['release']>): void {
    this.busyId.set(row.id);
    this.error.set(null);
    call.subscribe({
      next: () => { this.busyId.set(null); this.cancel(); this.load(); },
      error: (e: HttpErrorResponse) => { this.busyId.set(null); this.error.set(this.messageOf(e)); },
    });
  }

  private messageOf(e: HttpErrorResponse): string {
    const body = e.error as { message?: string; error?: string } | null;
    return body?.message || body?.error || 'The request failed. Try again.';
  }
}
