import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpContext, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, catchError, of, throwError } from 'rxjs';
import { EXECUTION_HISTORY_ENDPOINTS, WORKFLOW_ENDPOINTS } from '../../core/api-endpoints';
import { SKIP_LOADER } from '../../core/loading.interceptor';
import {
  FieldLineageChain,
  FieldLineageFilter,
  LineageSummary,
  NodeRunHistoryEntry,
  NodeRunPayloadDetail,
  PagedResult,
  ResourceHistoryEntry,
  ResourceTypeSummary,
  RouteExecution,
  RouteExecutionFilter,
  RouteExecutionPage,
  WorkflowRunStatusCounts,
} from '../models/execution-history.model';

@Injectable({ providedIn: 'root' })
export class ExecutionHistoryApiService {
  private readonly http = inject(HttpClient);

  // `silent` is opt-in and defaults to false — every existing caller (the real Execution History list
  // page included) keeps showing the global loader exactly as before. Only a background/periodic
  // refetch (Dashboard's auto-refresh, SignalR-triggered refresh) should ever pass true.
  list(filter: RouteExecutionFilter, options?: { silent?: boolean }): Observable<RouteExecutionPage> {
    let params = new HttpParams()
      .set('page', filter.page)
      .set('pageSize', filter.pageSize);

    if (filter.status) params = params.set('status', filter.status);
    if (filter.source) params = params.set('source', filter.source);
    for (const source of filter.sources ?? []) params = params.append('sources', source);
    if (filter.triggeredBy) params = params.set('triggeredBy', filter.triggeredBy);
    if (filter.search) params = params.set('search', filter.search);
    if (filter.sortColumn) params = params.set('sortColumn', filter.sortColumn);
    if (filter.sortDirection) params = params.set('sortDirection', filter.sortDirection);

    const context = options?.silent ? new HttpContext().set(SKIP_LOADER, true) : undefined;
    return this.http.get<RouteExecutionPage>(EXECUTION_HISTORY_ENDPOINTS.list, { params, context });
  }

  /** Requests a graceful stop of a still-running run — the currently in-flight node finishes normally, no
   *  further nodes start, and the run settles into a terminal Cancelled state. 409 (surfaced to the caller as
   *  an HttpErrorResponse) means the run already finished or was never started as a cancellable async run. */
  cancel(id: string): Observable<void> {
    return this.http.post<void>(WORKFLOW_ENDPOINTS.cancelRun(id), {});
  }

  byId(id: string): Observable<RouteExecution> {
    return this.http.get<RouteExecution>(EXECUTION_HISTORY_ENDPOINTS.byId(id));
  }

  resources(id: string, page = 1, pageSize = 25): Observable<PagedResult<ResourceHistoryEntry>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<ResourceHistoryEntry>>(EXECUTION_HISTORY_ENDPOINTS.resources(id), { params });
  }

  /** One row per node that actually started this run — success, failure, or cancellation always shown,
   *  unlike resources() which is silent about anything that didn't succeed. */
  nodeRuns(id: string, page = 1, pageSize = 25): Observable<PagedResult<NodeRunHistoryEntry>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<NodeRunHistoryEntry>>(EXECUTION_HISTORY_ENDPOINTS.nodeRuns(id), { params });
  }

  /** One node run's decrypted output — fetched lazily when its row is expanded, so the list above never pays
   *  the decryption cost for a node the user hasn't looked at. A 404 (node run never recorded a payload —
   *  still running, failed before producing output, or a None-contract node) resolves to null rather than
   *  erroring, since that's an expected, normal outcome here. */
  nodeRunPayload(id: string, nodeRunId: string): Observable<NodeRunPayloadDetail | null> {
    return this.http.get<NodeRunPayloadDetail>(EXECUTION_HISTORY_ENDPOINTS.nodeRunPayload(id, nodeRunId)).pipe(
      catchError((error: HttpErrorResponse) => error.status === 404 ? of(null) : throwError(() => error)),
    );
  }

  /** One row per (resource, destination field) touched by this run's transform-rule chain, each carrying its
   *  full source -> node -> node -> destination hop chain. filter backs the Group-by-Field/Patient/Node toggle
   *  and free-text search — all the same endpoint, just filtered differently. */
  fieldLineage(
    id: string, page = 1, pageSize = 25, filter?: FieldLineageFilter,
  ): Observable<PagedResult<FieldLineageChain>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (filter?.resourceType) params = params.set('resourceType', filter.resourceType);
    if (filter?.destinationField) params = params.set('destinationField', filter.destinationField);
    if (filter?.resourceId) params = params.set('resourceId', filter.resourceId);
    if (filter?.nodeType) params = params.set('nodeType', filter.nodeType);
    if (filter?.search) params = params.set('search', filter.search);

    return this.http.get<PagedResult<FieldLineageChain>>(EXECUTION_HISTORY_ENDPOINTS.fieldLineage(id), { params });
  }

  /** Run-wide field-lineage totals — backs the Lineage panel's stat strip. */
  lineageSummary(id: string): Observable<LineageSummary> {
    return this.http.get<LineageSummary>(EXECUTION_HISTORY_ENDPOINTS.lineageSummary(id));
  }

  /** Every resource type touched by this run's field lineage, with the destination fields under it — backs
   *  the Lineage panel's resource-tree sidebar. */
  lineageResourceTree(id: string): Observable<ResourceTypeSummary[]> {
    return this.http.get<ResourceTypeSummary[]>(EXECUTION_HISTORY_ENDPOINTS.lineageResourceTree(id));
  }

  /** All-time run count per status, across every workflow — backs the Dashboard's status stat tiles.
   *  See list()'s comment above re: `silent`. */
  statusCounts(options?: { silent?: boolean }): Observable<WorkflowRunStatusCounts> {
    const context = options?.silent ? new HttpContext().set(SKIP_LOADER, true) : undefined;
    return this.http.get<WorkflowRunStatusCounts>(EXECUTION_HISTORY_ENDPOINTS.statusCounts, { context });
  }
}
