import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { EXECUTION_HISTORY_ENDPOINTS } from '../../core/api-endpoints';
import {
  PagedResult,
  ResourceHistoryEntry,
  RouteExecution,
  RouteExecutionFilter,
} from '../models/execution-history.model';

@Injectable({ providedIn: 'root' })
export class ExecutionHistoryApiService {
  private readonly http = inject(HttpClient);

  list(filter: RouteExecutionFilter): Observable<PagedResult<RouteExecution>> {
    let params = new HttpParams()
      .set('page', filter.page)
      .set('pageSize', filter.pageSize);

    if (filter.status) params = params.set('status', filter.status);
    if (filter.source) params = params.set('source', filter.source);
    if (filter.triggeredBy) params = params.set('triggeredBy', filter.triggeredBy);
    if (filter.search) params = params.set('search', filter.search);

    return this.http.get<PagedResult<RouteExecution>>(EXECUTION_HISTORY_ENDPOINTS.list, { params });
  }

  byId(id: string): Observable<RouteExecution> {
    return this.http.get<RouteExecution>(EXECUTION_HISTORY_ENDPOINTS.byId(id));
  }

  resources(id: string, page = 1, pageSize = 25): Observable<PagedResult<ResourceHistoryEntry>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<ResourceHistoryEntry>>(EXECUTION_HISTORY_ENDPOINTS.resources(id), { params });
  }
}
