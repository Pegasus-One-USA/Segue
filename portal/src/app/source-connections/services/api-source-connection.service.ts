import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
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

  getPaged(filter: SourceConnectionFilter): Observable<PagedResult<SourceConnectionModel>> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.search) params = params.set('search', filter.search);
    if (filter.sourceSystemType) params = params.set('sourceSystemType', filter.sourceSystemType);
    if (filter.applicationType) params = params.set('applicationType', filter.applicationType);
    if (filter.isEnabled !== undefined) params = params.set('isEnabled', String(filter.isEnabled));
    if (filter.sortBy) params = params.set('sortBy', filter.sortBy);
    if (filter.sortOrder) params = params.set('sortOrder', filter.sortOrder);

    return this.http.get<PagedResult<SourceConnectionModel>>(SOURCE_CONNECTIONS_ENDPOINTS.paged, { params }).pipe(
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
}
