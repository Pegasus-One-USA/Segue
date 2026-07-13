import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { EHR_ENDPOINTS_ENDPOINTS } from '../../core/api-endpoints';
import { IEhrEndpointService } from './i-ehr-endpoint.service';
import { EhrEndpoint, EhrEndpointRequest } from '../models/ehr-endpoint.model';

@Injectable({ providedIn: 'root' })
export class ApiEhrEndpointService extends IEhrEndpointService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<EhrEndpoint[]> {
    return this.http.get<EhrEndpoint[]>(EHR_ENDPOINTS_ENDPOINTS.list).pipe(
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
