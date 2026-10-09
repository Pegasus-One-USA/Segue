import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { EHR_WRITE_LEDGER_ENDPOINTS } from '../core/api-endpoints';

/** EhrWriteLedgerReviewItemDto: one write awaiting a person. Ids, statuses and vendor codes only, never PHI. */
export interface EhrWriteReviewItem {
  id: string;
  targetConnectionId: string;
  targetConnectionName: string | null;
  targetVendor: string | null;
  resourceType: string;
  /** Unknown, Rejected, or Abandoned (a send that never finished). */
  state: string;
  targetResourceId: string | null;
  httpStatus: number | null;
  outcomeCodes: string | null;
  attemptCount: number;
  destinationId: string | null;
  workflowRunId: string | null;
  createdOnUtc: string;
  updatedOnUtc: string;
  reviewedBy: string | null;
  reviewedOnUtc: string | null;
}

export interface EhrWriteReviewPage {
  items: EhrWriteReviewItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

@Injectable({ providedIn: 'root' })
export class EhrWriteLedgerService {
  private readonly http = inject(HttpClient);

  review(resourceType: string | null, page: number, pageSize: number): Observable<EhrWriteReviewPage> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (resourceType) params = params.set('resourceType', resourceType);
    return this.http.get<EhrWriteReviewPage>(EHR_WRITE_LEDGER_ENDPOINTS.review, { params });
  }

  /** The record is in the EHR after all; records the EHR's id so it is never sent again. */
  markWritten(id: string, targetResourceId: string): Observable<EhrWriteReviewItem> {
    return this.http.post<EhrWriteReviewItem>(EHR_WRITE_LEDGER_ENDPOINTS.markWritten(id), { targetResourceId });
  }

  /** The record is not in the EHR; the next run sends it once more. */
  release(id: string): Observable<EhrWriteReviewItem> {
    return this.http.post<EhrWriteReviewItem>(EHR_WRITE_LEDGER_ENDPOINTS.release(id), {});
  }
}
