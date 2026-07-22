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
  QueueDepthEntry,
  RetryHistoryEntry,
  SchedulerHistoryEntry,
  SchedulerSummaryEntry,
  SystemHealth,
  ValidationFailureEntry,
} from '../models/operations.model';

function buildParams(correlationId: string | undefined, take: number): HttpParams {
  let params = new HttpParams().set('take', take);
  if (correlationId) {
    params = params.set('correlationId', correlationId);
  }
  return params;
}

@Injectable({ providedIn: 'root' })
export class OperationsApiService {
  private readonly http = inject(HttpClient);

  schedulerHistory(correlationId?: string, take = 200): Observable<SchedulerHistoryEntry[]> {
    return this.http.get<SchedulerHistoryEntry[]>(OPERATIONS_ENDPOINTS.schedulerHistory, { params: buildParams(correlationId, take) });
  }

  retryHistory(correlationId?: string, take = 200): Observable<RetryHistoryEntry[]> {
    return this.http.get<RetryHistoryEntry[]>(OPERATIONS_ENDPOINTS.retryHistory, { params: buildParams(correlationId, take) });
  }

  errors(correlationId?: string, take = 200): Observable<ErrorLogEntry[]> {
    return this.http.get<ErrorLogEntry[]>(OPERATIONS_ENDPOINTS.errors, { params: buildParams(correlationId, take) });
  }

  /** Phase 6A – multi-criteria error search (Monitoring → Errors). */
  searchErrors(search: ErrorLogSearch): Observable<ErrorLogEntry[]> {
    let params = new HttpParams().set('take', search.take ?? 200);
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
    return this.http.get<ErrorLogEntry[]>(OPERATIONS_ENDPOINTS.errors, { params });
  }

  /** Phase 6A – mark a captured error Resolved. */
  resolveError(errorReferenceId: string, notes?: string): Observable<void> {
    return this.http.post<void>(`${OPERATIONS_ENDPOINTS.errors}/${encodeURIComponent(errorReferenceId)}/resolve`, { notes: notes ?? null });
  }

  /** Phase 6A – reopen a resolved error. */
  reopenError(errorReferenceId: string): Observable<void> {
    return this.http.post<void>(`${OPERATIONS_ENDPOINTS.errors}/${encodeURIComponent(errorReferenceId)}/reopen`, {});
  }

  apiRequests(correlationId?: string, take = 200): Observable<ApiRequestLogEntry[]> {
    return this.http.get<ApiRequestLogEntry[]>(OPERATIONS_ENDPOINTS.apiRequests, { params: buildParams(correlationId, take) });
  }

  exports(correlationId?: string, take = 200): Observable<ExportHistoryEntry[]> {
    return this.http.get<ExportHistoryEntry[]>(OPERATIONS_ENDPOINTS.exports, { params: buildParams(correlationId, take) });
  }

  notifications(correlationId?: string, take = 200): Observable<NotificationHistoryEntry[]> {
    return this.http.get<NotificationHistoryEntry[]>(OPERATIONS_ENDPOINTS.notifications, { params: buildParams(correlationId, take) });
  }

  validationFailures(correlationId?: string, take = 200): Observable<ValidationFailureEntry[]> {
    return this.http.get<ValidationFailureEntry[]>(OPERATIONS_ENDPOINTS.validationFailures, { params: buildParams(correlationId, take) });
  }

  endpointHealth(take = 200): Observable<EndpointHealthCheckEntry[]> {
    return this.http.get<EndpointHealthCheckEntry[]>(OPERATIONS_ENDPOINTS.endpointHealth, { params: new HttpParams().set('take', take) });
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
