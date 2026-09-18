import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LICENSE_ENDPOINTS, LICENSE_REQUEST_ENDPOINTS } from '../../core/api-endpoints';
import {
  ApplyLicenseRequest, CreateLicenseRequestRequest, LicenseHistoryEntry, LicenseRequestStatus, LicenseStatus,
} from '../models/license.model';

@Injectable({ providedIn: 'root' })
export class LicenseService {
  private readonly http = inject(HttpClient);

  get(): Observable<LicenseStatus> {
    return this.http.get<LicenseStatus>(LICENSE_ENDPOINTS.get);
  }

  apply(request: ApplyLicenseRequest): Observable<LicenseStatus> {
    return this.http.post<LicenseStatus>(LICENSE_ENDPOINTS.apply, request);
  }

  getHistory(): Observable<LicenseHistoryEntry[]> {
    return this.http.get<LicenseHistoryEntry[]>(LICENSE_ENDPOINTS.history);
  }

  getRequest(): Observable<LicenseRequestStatus> {
    return this.http.get<LicenseRequestStatus>(LICENSE_REQUEST_ENDPOINTS.get);
  }

  createRequest(request: CreateLicenseRequestRequest): Observable<LicenseRequestStatus> {
    return this.http.post<LicenseRequestStatus>(LICENSE_REQUEST_ENDPOINTS.create, request);
  }

  resubmitRequest(): Observable<LicenseRequestStatus> {
    return this.http.post<LicenseRequestStatus>(LICENSE_REQUEST_ENDPOINTS.resubmit, {});
  }
}
