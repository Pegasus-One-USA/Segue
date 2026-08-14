import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MAPPING_PROFILE_ENDPOINTS } from '../../core/api-endpoints';
import {
  CreateMappingProfileRequest,
  MappingProfileDto,
  MappingProfileFilter,
  PagedResult,
} from '../models/mapping-profile.model';

/**
 * CRUD + usage gating for the standalone Mapping Profiles admin screen
 * (docs/backend/14-mapping-profile-master-screen-plan.md §5.2). Backed by ConfigurationsController
 * (create/update/activate/deactivate/delete) and ConfigurationCatalogController (paged list, get-by-id) —
 * see MAPPING_PROFILE_ENDPOINTS.
 */
@Injectable({ providedIn: 'root' })
export class MappingProfileService {
  private readonly http = inject(HttpClient);

  getPaged(filter: MappingProfileFilter): Observable<PagedResult<MappingProfileDto>> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.search) params = params.set('search', filter.search);
    if (filter.resourceType) params = params.set('resourceType', filter.resourceType);
    if (filter.sourceConnectionId) params = params.set('sourceConnectionId', filter.sourceConnectionId);
    if (filter.destinationId) params = params.set('destinationId', filter.destinationId);
    if (filter.isEnabled !== undefined) params = params.set('isEnabled', String(filter.isEnabled));
    if (filter.sortBy) params = params.set('sortBy', filter.sortBy);
    if (filter.sortOrder) params = params.set('sortOrder', filter.sortOrder);

    return this.http.get<PagedResult<MappingProfileDto>>(MAPPING_PROFILE_ENDPOINTS.paged, { params });
  }

  getById(id: string): Observable<MappingProfileDto> {
    return this.http.get<MappingProfileDto>(MAPPING_PROFILE_ENDPOINTS.byId(id));
  }

  create(request: CreateMappingProfileRequest): Observable<MappingProfileDto> {
    return this.http.post<MappingProfileDto>(MAPPING_PROFILE_ENDPOINTS.list, request);
  }

  update(id: string, request: CreateMappingProfileRequest): Observable<MappingProfileDto> {
    return this.http.put<MappingProfileDto>(MAPPING_PROFILE_ENDPOINTS.byId(id), request);
  }

  activate(id: string): Observable<MappingProfileDto> {
    return this.http.post<MappingProfileDto>(MAPPING_PROFILE_ENDPOINTS.activate(id), {});
  }

  deactivate(id: string): Observable<MappingProfileDto> {
    return this.http.post<MappingProfileDto>(MAPPING_PROFILE_ENDPOINTS.deactivate(id), {});
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(MAPPING_PROFILE_ENDPOINTS.byId(id));
  }

  /** "Mark as Master" — clones a workflow's own mapping profile into a brand-new, independently-named master
   *  template other workflows can later find via "Select Existing". Always creates a new profile; never
   *  overwrites an existing one, even one that already matches the same resource type/source/destination. */
  promoteToMaster(id: string, name: string): Observable<MappingProfileDto> {
    return this.http.post<MappingProfileDto>(MAPPING_PROFILE_ENDPOINTS.promoteToMaster(id), { name });
  }

  /** Every mapping profile id currently referenced by at least one workflow (node config or persisted route) —
   *  see MAPPING_PROFILE_ENDPOINTS.usage. Used to gate Delete independently of the server's own route-usage
   *  check (which is the actual guard — this only lets the UI show *why* before the user even tries). */
  getUsedInWorkflowIds(): Observable<string[]> {
    return this.http.get<string[]>(MAPPING_PROFILE_ENDPOINTS.usage);
  }
}
