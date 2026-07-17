import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { SOURCE_CONNECTIONS_ENDPOINTS } from '../../core/api-endpoints';
import { ISourceConnectionService } from './i-source-connection.service';
import { SourceConnectionModel, SourceConnectionRequest } from '../models/source-connection.model';

@Injectable({ providedIn: 'root' })
export class ApiSourceConnectionService extends ISourceConnectionService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<SourceConnectionModel[]> {
    return this.http.get<SourceConnectionModel[]>(SOURCE_CONNECTIONS_ENDPOINTS.list).pipe(
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
}
