import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LINEAGE_ENDPOINTS } from '../../core/api-endpoints';
import { LineageChain, LineageChainQuery, LineageEntry, LineageFilter, PagedResult } from '../models/lineage.model';

@Injectable({ providedIn: 'root' })
export class LineageApiService {
  private readonly http = inject(HttpClient);

  list(filter: LineageFilter): Observable<PagedResult<LineageEntry>> {
    let params = new HttpParams()
      .set('page', filter.page)
      .set('pageSize', filter.pageSize);

    if (filter.pipelineRunId) params = params.set('pipelineRunId', filter.pipelineRunId);
    if (filter.resourceType) params = params.set('resourceType', filter.resourceType);
    if (filter.action) params = params.set('action', filter.action);
    if (filter.status) params = params.set('status', filter.status);

    return this.http.get<PagedResult<LineageEntry>>(LINEAGE_ENDPOINTS.list, { params });
  }

  chain(query: LineageChainQuery): Observable<LineageChain> {
    let params = new HttpParams();
    if (query.pipelineRunId) params = params.set('pipelineRunId', query.pipelineRunId);
    if (query.resourceType) params = params.set('resourceType', query.resourceType);
    if (query.sourceResourceId) params = params.set('sourceResourceId', query.sourceResourceId);

    return this.http.get<LineageChain>(LINEAGE_ENDPOINTS.chain, { params });
  }
}
