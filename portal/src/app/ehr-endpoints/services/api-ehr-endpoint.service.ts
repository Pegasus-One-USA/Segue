import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams, HttpContext } from '@angular/common/http';
import { SKIP_LOADER } from '../../core/loading.interceptor';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { EHR_ENDPOINTS_ENDPOINTS } from '../../core/api-endpoints';
import { IEhrEndpointService } from './i-ehr-endpoint.service';
import { EhrEndpoint, EhrEndpointFilter, EhrEndpointPage, EhrEndpointRequest } from '../models/ehr-endpoint.model';

@Injectable({ providedIn: 'root' })
export class ApiEhrEndpointService extends IEhrEndpointService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<EhrEndpoint[]> {
    return this.http.get<EhrEndpoint[]>(EHR_ENDPOINTS_ENDPOINTS.list).pipe(
      catchError(err => throwError(() => err))
    );
  }

  /** `silent` (passed only from a debounced search-box keystroke) sends SKIP_LOADER so this request
   *  bypasses the app-wide global loader — which dims the screen and marks the routed content [inert],
   *  blurring the very input being typed into. The caller shows a small in-field spinner instead. */
  getPaged(filter: EhrEndpointFilter, silent = false): Observable<EhrEndpointPage> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.search) params = params.set('search', filter.search);
    if (filter.vendor) params = params.set('vendor', filter.vendor);
    if (filter.isActive !== undefined) params = params.set('isActive', String(filter.isActive));
    if (filter.sortDescending !== undefined) params = params.set('sortDescending', String(filter.sortDescending));

    const context = silent ? new HttpContext().set(SKIP_LOADER, true) : undefined;
    return this.http.get<EhrEndpointPage>(EHR_ENDPOINTS_ENDPOINTS.paged, { params, context }).pipe(
      catchError(err => throwError(() => err))
    );
  }

  getById(id: string): Observable<EhrEndpoint> {
    return this.http.get<EhrEndpoint>(EHR_ENDPOINTS_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }

  create(req: EhrEndpointRequest): Observable<EhrEndpoint> {
    return this.http.post<EhrEndpoint>(EHR_ENDPOINTS_ENDPOINTS.list, req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  update(id: string, req: EhrEndpointRequest): Observable<EhrEndpoint> {
    return this.http.put<EhrEndpoint>(EHR_ENDPOINTS_ENDPOINTS.byId(id), req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(EHR_ENDPOINTS_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }
}
