import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { CORS_ORIGINS_ENDPOINTS } from '../../core/api-endpoints';
import { IAllowedCorsOriginService } from './i-allowed-cors-origin.service';
import { AllowedCorsOrigin, CreateAllowedCorsOriginRequest, UpdateAllowedCorsOriginRequest } from '../models/allowed-cors-origin.model';

@Injectable({ providedIn: 'root' })
export class ApiAllowedCorsOriginService extends IAllowedCorsOriginService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<AllowedCorsOrigin[]> {
    return this.http.get<AllowedCorsOrigin[]>(CORS_ORIGINS_ENDPOINTS.list).pipe(
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
