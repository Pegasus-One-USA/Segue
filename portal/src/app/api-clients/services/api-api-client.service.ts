import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams, HttpContext } from '@angular/common/http';
import { SKIP_LOADER } from '../../core/loading.interceptor';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { API_CLIENTS_ENDPOINTS } from '../../core/api-endpoints';
import { IApiClientService } from './i-api-client.service';
import {
  AddApiClientReturnUrlRequest,
  ApiClient,
  ApiClientCredential,
  ApiClientFilter,
  ApiClientPage,
  CreateApiClientRequest,
  UpdateApiClientRequest,
} from '../models/api-client.model';

@Injectable({ providedIn: 'root' })
export class ApiApiClientService extends IApiClientService {
  private readonly http = inject(HttpClient);

  /** `silent` (passed only from a debounced search-box keystroke) sends SKIP_LOADER so this request
   *  bypasses the app-wide global loader — see ApiAllowedCorsOriginService's matching remark. */
  getPaged(filter: ApiClientFilter, silent = false): Observable<ApiClientPage> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.search) params = params.set('search', filter.search);

    const context = silent ? new HttpContext().set(SKIP_LOADER, true) : undefined;
    return this.http.get<ApiClientPage>(API_CLIENTS_ENDPOINTS.paged, { params, context }).pipe(
      catchError(err => throwError(() => err))
    );
  }

  create(req: CreateApiClientRequest): Observable<ApiClientCredential> {
    return this.http.post<ApiClientCredential>(API_CLIENTS_ENDPOINTS.paged, req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  regenerateSecret(id: string): Observable<ApiClientCredential> {
    return this.http.post<ApiClientCredential>(API_CLIENTS_ENDPOINTS.regenerateSecret(id), {}).pipe(
      catchError(err => throwError(() => err))
    );
  }

  update(id: string, req: UpdateApiClientRequest): Observable<ApiClient> {
    return this.http.put<ApiClient>(API_CLIENTS_ENDPOINTS.byId(id), req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(API_CLIENTS_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }

  addReturnUrl(id: string, req: AddApiClientReturnUrlRequest): Observable<ApiClient> {
    return this.http.post<ApiClient>(API_CLIENTS_ENDPOINTS.returnUrls(id), req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  removeReturnUrl(id: string, returnUrlId: string): Observable<ApiClient> {
    return this.http.delete<ApiClient>(API_CLIENTS_ENDPOINTS.returnUrlById(id, returnUrlId)).pipe(
      catchError(err => throwError(() => err))
    );
  }
}
