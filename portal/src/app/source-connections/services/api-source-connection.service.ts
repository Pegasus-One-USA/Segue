import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams, HttpContext } from '@angular/common/http';
import { SKIP_LOADER } from '../../core/loading.interceptor';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { SOURCE_CONNECTIONS_ENDPOINTS } from '../../core/api-endpoints';
import { ISourceConnectionService } from './i-source-connection.service';
import {
  GeneratedSigningKeyModel,
  PagedResult,
  SourceConnectionFilter,
  SourceConnectionModel,
  SourceConnectionRequest,
} from '../models/source-connection.model';

@Injectable({ providedIn: 'root' })
export class ApiSourceConnectionService extends ISourceConnectionService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<SourceConnectionModel[]> {
    return this.http.get<SourceConnectionModel[]>(SOURCE_CONNECTIONS_ENDPOINTS.list).pipe(
      catchError(err => throwError(() => err))
    );
  }

  /** `silent` (passed only from a debounced search-box keystroke) sends SKIP_LOADER so this request
   *  bypasses the app-wide global loader — which dims the screen and marks the routed content [inert],
   *  blurring the very input being typed into. The caller shows a small in-field spinner instead. */
  getPaged(filter: SourceConnectionFilter, silent = false): Observable<PagedResult<SourceConnectionModel>> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.search) params = params.set('search', filter.search);
    if (filter.sourceSystemType) params = params.set('sourceSystemType', filter.sourceSystemType);
    if (filter.applicationType) params = params.set('applicationType', filter.applicationType);
    if (filter.isEnabled !== undefined) params = params.set('isEnabled', String(filter.isEnabled));
    if (filter.sortBy) params = params.set('sortBy', filter.sortBy);
    if (filter.sortOrder) params = params.set('sortOrder', filter.sortOrder);

    const context = silent ? new HttpContext().set(SKIP_LOADER, true) : undefined;
    return this.http.get<PagedResult<SourceConnectionModel>>(SOURCE_CONNECTIONS_ENDPOINTS.paged, { params, context }).pipe(
      catchError(err => throwError(() => err))
    );
  }

  getById(id: string): Observable<SourceConnectionModel> {
    return this.http.get<SourceConnectionModel>(SOURCE_CONNECTIONS_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }

  create(req: SourceConnectionRequest): Observable<SourceConnectionModel> {
    return this.http.post<SourceConnectionModel>(SOURCE_CONNECTIONS_ENDPOINTS.list, req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  update(id: string, req: SourceConnectionRequest): Observable<SourceConnectionModel> {
    return this.http.put<SourceConnectionModel>(SOURCE_CONNECTIONS_ENDPOINTS.byId(id), req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(SOURCE_CONNECTIONS_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }

  getUsedIds(): Observable<string[]> {
    return this.http.get<string[]>(SOURCE_CONNECTIONS_ENDPOINTS.usage).pipe(
      catchError(err => throwError(() => err))
    );
  }

  generateSigningKey(): Observable<GeneratedSigningKeyModel> {
    return this.http.post<GeneratedSigningKeyModel>(SOURCE_CONNECTIONS_ENDPOINTS.generateSigningKey, {}).pipe(
      catchError(err => throwError(() => err))
    );
  }

  importSigningKey(privateKeyPem: string): Observable<GeneratedSigningKeyModel> {
    return this.http.post<GeneratedSigningKeyModel>(
      SOURCE_CONNECTIONS_ENDPOINTS.importSigningKey,
      { privateKeyPem }
    ).pipe(
      catchError(err => throwError(() => err))
    );
  }

  downloadPublicKeyPem(sourceConnectionId: string): Observable<Blob> {
    return this.http.get(SOURCE_CONNECTIONS_ENDPOINTS.publicKeyPem(sourceConnectionId), { responseType: 'blob' }).pipe(
      catchError(err => throwError(() => err))
    );
  }

  downloadPrivateKeyPem(sourceConnectionId: string): Observable<Blob> {
    return this.http.get(SOURCE_CONNECTIONS_ENDPOINTS.privateKeyPem(sourceConnectionId), { responseType: 'blob' }).pipe(
      catchError(err => throwError(() => err))
    );
  }
}
