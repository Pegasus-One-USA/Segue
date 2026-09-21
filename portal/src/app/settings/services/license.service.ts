import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LICENSE_ENDPOINTS, LICENSE_REQUEST_ENDPOINTS } from '../../core/api-endpoints';
import {
  ApplyLicenseRequest, CreateLicenseRequestRequest, LicenseHistoryEntry, LicenseRequestStatus, LicenseStatus,
  LicensorApplicationUrl,
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

  getRequests(): Observable<LicenseRequestStatus[]> {
    return this.http.get<LicenseRequestStatus[]>(LICENSE_REQUEST_ENDPOINTS.get);
  }

  createRequest(request: CreateLicenseRequestRequest): Observable<LicenseRequestStatus> {
    return this.http.post<LicenseRequestStatus>(LICENSE_REQUEST_ENDPOINTS.create, request);
  }

  updateRequest(id: string, request: CreateLicenseRequestRequest): Observable<LicenseRequestStatus> {
    return this.http.put<LicenseRequestStatus>(LICENSE_REQUEST_ENDPOINTS.update(id), request);
  }

  /** Hides the request from the list without physically deleting it (soft delete). */
  deleteRequest(id: string): Observable<void> {
    return this.http.delete<void>(LICENSE_REQUEST_ENDPOINTS.delete(id));
  }

  // Narrow read/write of just the licensor URL — reachable while unlicensed (see the endpoint's own
  // comment); the general-purpose ISystemSettingsService.getAll()/set() is NOT, since the license gate
  // does not allowlist /api/v1/system/settings.
  getLicensorUrl(): Observable<LicensorApplicationUrl> {
    return this.http.get<LicensorApplicationUrl>(LICENSE_REQUEST_ENDPOINTS.licensorUrl);
  }

  setLicensorUrl(url: string): Observable<LicensorApplicationUrl> {
    return this.http.put<LicensorApplicationUrl>(LICENSE_REQUEST_ENDPOINTS.licensorUrl, { url });
  }

  // TESTING/SUPPORT UTILITY ONLY — see LicenseController.Clear/ClearHistory. Never called from the
  // normal apply flow.
  clear(): Observable<LicenseStatus> {
    return this.http.delete<LicenseStatus>(LICENSE_ENDPOINTS.clear);
  }

  clearHistory(): Observable<void> {
    return this.http.delete<void>(LICENSE_ENDPOINTS.clearHistory);
  }
}
