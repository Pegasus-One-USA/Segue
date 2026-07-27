import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { OPERATIONS_ENDPOINTS } from '../../core/api-endpoints';
import {
  ApiAnalytics,
  ApiRequestLogEntry,
  EndpointHealthCheckEntry,
  ErrorLogEntry,
  ErrorLogSearch,
  ExportHistoryEntry,
  NotificationHistoryEntry,
  PagedResult,
  QueueDepthEntry,
  RetryHistoryEntry,
  SchedulerHistoryEntry,
  SchedulerSummaryEntry,
  SystemHealth,
  ValidationFailureEntry,
} from '../models/operations.model';

function buildParams(correlationId: string | undefined, page: number, pageSize: number): HttpParams {
  let params = new HttpParams()
    .set('skip', (Math.max(page, 1) - 1) * pageSize)
    .set('take', pageSize);
  if (correlationId) {
    params = params.set('correlationId', correlationId);
  }
  return params;
}

@Injectable({ providedIn: 'root' })
export class OperationsApiService {
  private readonly http = inject(HttpClient);

  schedulerHistory(correlationId: string | undefined, page: number, pageSize: number): Observable<PagedResult<SchedulerHistoryEntry>> {
    return this.http.get<PagedResult<SchedulerHistoryEntry>>(OPERATIONS_ENDPOINTS.schedulerHistory, { params: buildParams(correlationId, page, pageSize) });
  }

  retryHistory(correlationId: string | undefined, page: number, pageSize: number): Observable<PagedResult<RetryHistoryEntry>> {
    return this.http.get<PagedResult<RetryHistoryEntry>>(OPERATIONS_ENDPOINTS.retryHistory, { params: buildParams(correlationId, page, pageSize) });
  }

  errors(correlationId: string | undefined, page: number, pageSize: number): Observable<PagedResult<ErrorLogEntry>> {
    return this.http.get<PagedResult<ErrorLogEntry>>(OPERATIONS_ENDPOINTS.errors, { params: buildParams(correlationId, page, pageSize) });
  }

  /** Phase 6A – multi-criteria error search (Monitoring → Errors). */
  searchErrors(search: ErrorLogSearch): Observable<PagedResult<ErrorLogEntry>> {
    const page = search.page ?? 1;
    const pageSize = search.pageSize ?? 25;
    let params = new HttpParams()
      .set('skip', (Math.max(page, 1) - 1) * pageSize)
      .set('take', pageSize);
    const set = (key: string, value: string | undefined) => {
      if (value) { params = params.set(key, value); }
    };
    set('errorReferenceId', search.errorReferenceId);
    set('correlationId', search.correlationId);
    set('executionId', search.executionId);
    set('workflowId', search.workflowId);
    set('endpointId', search.endpointId);
    set('severity', search.severity);
    set('category', search.category);
    set('status', search.status);
    set('fromUtc', search.fromUtc);
    set('toUtc', search.toUtc);
    return this.http.get<PagedResult<ErrorLogEntry>>(OPERATIONS_ENDPOINTS.errors, { params });
  }

  /** Phase 6A – mark a captured error Resolved. */
  resolveError(errorReferenceId: string, notes?: string): Observable<void> {
    return this.http.post<void>(`${OPERATIONS_ENDPOINTS.errors}/${encodeURIComponent(errorReferenceId)}/resolve`, { notes: notes ?? null });
  }

  /** Phase 6A – reopen a resolved error. */
  reopenError(errorReferenceId: string): Observable<void> {
    return this.http.post<void>(`${OPERATIONS_ENDPOINTS.errors}/${encodeURIComponent(errorReferenceId)}/reopen`, {});
  }

  apiRequests(correlationId: string | undefined, page: number, pageSize: number): Observable<PagedResult<ApiRequestLogEntry>> {
    return this.http.get<PagedResult<ApiRequestLogEntry>>(OPERATIONS_ENDPOINTS.apiRequests, { params: buildParams(correlationId, page, pageSize) });
  }

  exports(correlationId: string | undefined, page: number, pageSize: number): Observable<PagedResult<ExportHistoryEntry>> {
    return this.http.get<PagedResult<ExportHistoryEntry>>(OPERATIONS_ENDPOINTS.exports, { params: buildParams(correlationId, page, pageSize) });
  }

  notifications(correlationId: string | undefined, page: number, pageSize: number): Observable<PagedResult<NotificationHistoryEntry>> {
    return this.http.get<PagedResult<NotificationHistoryEntry>>(OPERATIONS_ENDPOINTS.notifications, { params: buildParams(correlationId, page, pageSize) });
  }

  validationFailures(correlationId: string | undefined, page: number, pageSize: number): Observable<PagedResult<ValidationFailureEntry>> {
    return this.http.get<PagedResult<ValidationFailureEntry>>(OPERATIONS_ENDPOINTS.validationFailures, { params: buildParams(correlationId, page, pageSize) });
  }

  endpointHealth(page: number, pageSize: number): Observable<PagedResult<EndpointHealthCheckEntry>> {
    const params = new HttpParams()
      .set('skip', (Math.max(page, 1) - 1) * pageSize)
      .set('take', pageSize);
    return this.http.get<PagedResult<EndpointHealthCheckEntry>>(OPERATIONS_ENDPOINTS.endpointHealth, { params });
  }

  queueMonitor(): Observable<QueueDepthEntry[]> {
    return this.http.get<QueueDepthEntry[]>(OPERATIONS_ENDPOINTS.queueMonitor);
  }

  apiAnalytics(): Observable<ApiAnalytics> {
    return this.http.get<ApiAnalytics>(OPERATIONS_ENDPOINTS.apiAnalytics);
  }

  systemHealth(): Observable<SystemHealth> {
    return this.http.get<SystemHealth>(OPERATIONS_ENDPOINTS.systemHealth);
  }

  schedulerSummary(): Observable<SchedulerSummaryEntry[]> {
    return this.http.get<SchedulerSummaryEntry[]>(OPERATIONS_ENDPOINTS.schedulerSummary);
  }
}
