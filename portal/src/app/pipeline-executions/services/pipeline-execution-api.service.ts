import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { PIPELINE_RUNS_ENDPOINTS } from '../../core/api-endpoints';
import {
  PipelineExecutionEntry,
  PipelineExecutionPagedResult,
  PipelineResourceHistoryPagedResult,
} from '../models/pipeline-execution.model';

@Injectable({ providedIn: 'root' })
export class PipelineExecutionApiService {
  private readonly http = inject(HttpClient);

  list(
    page = 1,
    pageSize = 25,
    status?: string,
    source?: string,
    triggeredBy?: string,
    search?: string,
  ): Observable<PipelineExecutionPagedResult> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (status) params = params.set('status', status);
    if (source) params = params.set('source', source);
    if (triggeredBy) params = params.set('triggeredBy', triggeredBy);
    if (search) params = params.set('search', search);
    return this.http.get<PipelineExecutionPagedResult>(PIPELINE_RUNS_ENDPOINTS.routeExecutions, { params });
  }

  byId(routeExecutionId: string): Observable<PipelineExecutionEntry> {
    return this.http.get<PipelineExecutionEntry>(PIPELINE_RUNS_ENDPOINTS.routeExecutionById(routeExecutionId));
  }

  resources(routeExecutionId: string, page = 1, pageSize = 25): Observable<PipelineResourceHistoryPagedResult> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PipelineResourceHistoryPagedResult>(
      PIPELINE_RUNS_ENDPOINTS.routeExecutionResources(routeExecutionId), { params });
  }
}
