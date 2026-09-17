import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams, HttpContext } from '@angular/common/http';
import { SKIP_LOADER } from '../../core/loading.interceptor';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { CORS_ORIGINS_ENDPOINTS } from '../../core/api-endpoints';
import { IAllowedCorsOriginService } from './i-allowed-cors-origin.service';
import {
  AllowedCorsOrigin,
  AllowedCorsOriginFilter,
  AllowedCorsOriginPage,
  CreateAllowedCorsOriginRequest,
  UpdateAllowedCorsOriginRequest,
} from '../models/allowed-cors-origin.model';

@Injectable({ providedIn: 'root' })
export class ApiAllowedCorsOriginService extends IAllowedCorsOriginService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<AllowedCorsOrigin[]> {
    return this.http.get<AllowedCorsOrigin[]>(CORS_ORIGINS_ENDPOINTS.list).pipe(
      catchError(err => throwError(() => err))
    );
  }

  /** `silent` (passed only from a debounced search-box keystroke) sends SKIP_LOADER so this request
   *  bypasses the app-wide global loader — which dims the screen and marks the routed content [inert],
   *  blurring the very input being typed into. The caller shows a small in-field spinner instead. */
  getPaged(filter: AllowedCorsOriginFilter, silent = false): Observable<AllowedCorsOriginPage> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.search) params = params.set('search', filter.search);

    const context = silent ? new HttpContext().set(SKIP_LOADER, true) : undefined;
    return this.http.get<AllowedCorsOriginPage>(CORS_ORIGINS_ENDPOINTS.paged, { params, context }).pipe(
      catchError(err => throwError(() => err))
    );
  }

  create(req: CreateAllowedCorsOriginRequest): Observable<AllowedCorsOrigin> {
    return this.http.post<AllowedCorsOrigin>(CORS_ORIGINS_ENDPOINTS.list, req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  update(id: string, req: UpdateAllowedCorsOriginRequest): Observable<AllowedCorsOrigin> {
    return this.http.put<AllowedCorsOrigin>(CORS_ORIGINS_ENDPOINTS.byId(id), req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(CORS_ORIGINS_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }
}
