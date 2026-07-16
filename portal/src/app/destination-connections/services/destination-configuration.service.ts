import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { DESTINATION_ENDPOINTS } from '../../core/api-endpoints';
import {
  CreateDestinationConfigurationRequest,
  DestinationConfigurationDto,
  DestinationConfigurationFilter,
  PagedResult,
} from '../models/destination-configuration.model';

export interface DestinationExecutionHistory {
  hasExecutionHistory: boolean;
}

/**
 * CRUD + execution-history gating for the standalone Destination Connections admin screen. Backed by
 * ConfigurationsController (create/update/delete) and ConfigurationCatalogController (paged list,
 * execution-history check) — see api-endpoints.ts DESTINATION_ENDPOINTS.
 */
@Injectable({ providedIn: 'root' })
export class DestinationConfigurationService {
  private readonly http = inject(HttpClient);

  getPaged(filter: DestinationConfigurationFilter): Observable<PagedResult<DestinationConfigurationDto>> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.search) params = params.set('search', filter.search);
    if (filter.destinationType) params = params.set('destinationType', filter.destinationType);
    if (filter.isEnabled !== undefined) params = params.set('isEnabled', String(filter.isEnabled));

    return this.http.get<PagedResult<DestinationConfigurationDto>>(DESTINATION_ENDPOINTS.paged, { params });
  }

  create(request: CreateDestinationConfigurationRequest): Observable<DestinationConfigurationDto> {
    return this.http.post<DestinationConfigurationDto>(DESTINATION_ENDPOINTS.list, request);
  }

  update(id: string, request: CreateDestinationConfigurationRequest): Observable<DestinationConfigurationDto> {
    return this.http.put<DestinationConfigurationDto>(DESTINATION_ENDPOINTS.byId(id), request);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(DESTINATION_ENDPOINTS.byId(id));
  }

  hasExecutionHistory(id: string): Observable<DestinationExecutionHistory> {
    return this.http.get<DestinationExecutionHistory>(DESTINATION_ENDPOINTS.hasExecutionHistory(id));
  }

  /** Every destination id currently referenced by at least one workflow's Destination node, regardless of whether
   *  that workflow has ever run — see DESTINATION_ENDPOINTS.usage. Used to gate Delete independently of
   *  hasExecutionHistory, which only reflects run history. */
  getUsedInWorkflowIds(): Observable<string[]> {
    return this.http.get<string[]>(DESTINATION_ENDPOINTS.usage);
  }
}
