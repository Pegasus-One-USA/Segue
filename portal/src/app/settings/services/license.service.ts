import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LICENSE_ENDPOINTS } from '../../core/api-endpoints';
import { ApplyLicenseRequest, LicenseStatus } from '../models/license.model';

@Injectable({ providedIn: 'root' })
export class LicenseService {
  private readonly http = inject(HttpClient);

  get(): Observable<LicenseStatus> {
    return this.http.get<LicenseStatus>(LICENSE_ENDPOINTS.get);
  }

  apply(request: ApplyLicenseRequest): Observable<LicenseStatus> {
    return this.http.post<LicenseStatus>(LICENSE_ENDPOINTS.apply, request);
  }
}
