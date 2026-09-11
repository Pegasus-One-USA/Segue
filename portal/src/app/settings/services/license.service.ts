import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LICENSE_ENDPOINTS } from '../../core/api-endpoints';
import { ApplyLicenseRequest, DevLicenseMintRequest, DevLicenseMintResponse, LicenseStatus } from '../models/license.model';

@Injectable({ providedIn: 'root' })
export class LicenseService {
  private readonly http = inject(HttpClient);

  get(): Observable<LicenseStatus> {
    return this.http.get<LicenseStatus>(LICENSE_ENDPOINTS.get);
  }

  apply(request: ApplyLicenseRequest): Observable<LicenseStatus> {
    return this.http.post<LicenseStatus>(LICENSE_ENDPOINTS.apply, request);
  }

  /** ⚠ TEMPORARY / DEV-ONLY — calls the throwaway license-minting endpoint backing the
   *  "Dev: Mint a test license" page (settings/license/mint-dev). The backend 404s this call on any
   *  non-Development host; that server-side gate is the real security boundary here. Delete this
   *  method (and DevLicenseMintRequest/DevLicenseMintResponse, LICENSE_ENDPOINTS.devMint, and the
   *  license-dev-mint page) once license minting moves to its own separate internal tool. */
  mintDev(request: DevLicenseMintRequest): Observable<DevLicenseMintResponse> {
    return this.http.post<DevLicenseMintResponse>(LICENSE_ENDPOINTS.devMint, request);
  }
}
