import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { OPERATIONAL_LOGS_ENDPOINTS } from '../../core/api-endpoints';
import { OperationalLog, OperationalLogFilter, PagedResult } from '../models/operational-log.model';

@Injectable({ providedIn: 'root' })
export class OperationalLogsApiService {
  private readonly http = inject(HttpClient);

  list(filter: OperationalLogFilter): Observable<PagedResult<OperationalLog>> {
    let params = new HttpParams()
      .set('page', filter.page)
      .set('pageSize', filter.pageSize);

    if (filter.pipelineRunId) params = params.set('pipelineRunId', filter.pipelineRunId);
    if (filter.resourceType) params = params.set('resourceType', filter.resourceType);
    if (filter.action) params = params.set('action', filter.action);
    if (filter.status) params = params.set('status', filter.status);
    if (filter.severity) params = params.set('severity', filter.severity);
    if (filter.search) params = params.set('search', filter.search);

    return this.http.get<PagedResult<OperationalLog>>(OPERATIONAL_LOGS_ENDPOINTS.list, { params });
  }
}
