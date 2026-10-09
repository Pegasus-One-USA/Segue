import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { OPERATIONS_ENDPOINTS } from '../../core/api-endpoints';
import {
  ApiAnalytics,
  ApiRequestLogEntry,
  EndpointHealthCheckEntry,
  ErrorCorrelation,
  ErrorLogDeleteResult,
  ErrorLogPurgeResult,
  ErrorLogSettings,
  ErrorLogSettingsResponse,
  ErrorLogStorage,
  ErrorDashboard,
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

  /** Error Dashboard – aggregated counts, trend and recurring errors for a period. */
  errorDashboard(
    fromUtc: string | undefined, toUtc: string | undefined, severity?: string, category?: string,
  ): Observable<ErrorDashboard> {
    let params = new HttpParams();
    if (fromUtc) { params = params.set('fromUtc', fromUtc); }
    if (toUtc) { params = params.set('toUtc', toUtc); }
    if (severity) { params = params.set('severity', severity); }
    if (category) { params = params.set('category', category); }
    return this.http.get<ErrorDashboard>(OPERATIONS_ENDPOINTS.errorDashboard, { params });
  }

  /** Permanently deletes errors ('all', or those before a date) and releases the space they used. */
  deleteErrors(mode: 'all' | 'olderThan', olderThanUtc: string | null): Observable<ErrorLogDeleteResult> {
    return this.http.post<ErrorLogDeleteResult>(OPERATIONS_ENDPOINTS.errorDelete, { mode, olderThanUtc });
  }

  errorLogSettings(): Observable<ErrorLogSettingsResponse> {
    return this.http.get<ErrorLogSettingsResponse>(OPERATIONS_ENDPOINTS.errorLogSettings);
  }

  saveErrorLogSettings(settings: ErrorLogSettings): Observable<ErrorLogSettingsResponse> {
    return this.http.put<ErrorLogSettingsResponse>(OPERATIONS_ENDPOINTS.errorLogSettings, settings);
  }

  errorLogStorage(): Observable<ErrorLogStorage> {
    return this.http.get<ErrorLogStorage>(OPERATIONS_ENDPOINTS.errorLogStorage);
  }

  /** Deletes entries older than the saved retention period right now. */
  purgeErrorLog(): Observable<ErrorLogPurgeResult> {
    return this.http.post<ErrorLogPurgeResult>(OPERATIONS_ENDPOINTS.errorLogPurge, {});
  }

  /** Error detail – the run timeline around one correlation id. */
  errorCorrelation(correlationId: string): Observable<ErrorCorrelation> {
    return this.http.get<ErrorCorrelation>(OPERATIONS_ENDPOINTS.errorCorrelation(correlationId));
  }

  /** Error Dashboard – hides everything recorded so far (nothing is deleted). */
  /** Hides errors recorded before the given instant (omitted = everything so far) from the Overview. */
  clearErrorDashboard(beforeUtc?: string): Observable<void> {
    return this.http.post<void>(OPERATIONS_ENDPOINTS.errorDashboardClear, { beforeUtc: beforeUtc ?? null });
  }

  /** Error Dashboard – shows previously cleared errors again. */
  restoreErrorDashboard(): Observable<void> {
    return this.http.post<void>(OPERATIONS_ENDPOINTS.errorDashboardRestore, {});
  }

  /** Error Dashboard – downloads the PHI-scrubbed error report the client can share with support. */
  exportErrorReport(
    format: 'json' | 'csv',
    fromUtc: string | undefined,
    toUtc: string | undefined,
    includeStackTrace: boolean,
    filters: { severity?: string; category?: string; status?: string } = {},
    includeCorrelation = true,
  ): Observable<Blob> {
    let params = new HttpParams().set('format', format).set('includeStackTrace', includeStackTrace)
      .set('includeCorrelation', includeCorrelation);
    if (fromUtc) { params = params.set('fromUtc', fromUtc); }
    if (toUtc) { params = params.set('toUtc', toUtc); }
    if (filters.severity) { params = params.set('severity', filters.severity); }
    if (filters.category) { params = params.set('category', filters.category); }
    if (filters.status) { params = params.set('status', filters.status); }
    return this.http.get(OPERATIONS_ENDPOINTS.errorExport, { params, responseType: 'blob' });
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
