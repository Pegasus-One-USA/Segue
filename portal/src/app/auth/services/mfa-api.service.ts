/**
 * MfaApiService — HTTP client for the authenticated MFA management endpoints (enroll/verify/
 * disable/status), wired to MFA_ENDPOINTS (see core/api-endpoints.ts). Login-time MFA (the
 * challenge/verify step of signing in) is handled separately by AuthApiService.verifyMfaLogin,
 * since that path is anonymous and returns a full session rather than a management response.
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MFA_ENDPOINTS } from '../../core/api-endpoints';
import {
  MfaStatusResponse,
  MfaEnrollmentResponse,
  MfaCodeRequest,
  MfaEnrollmentConfirmedResponse,
} from '../models/mfa.model';

@Injectable({ providedIn: 'root' })
export class MfaApiService {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<MfaStatusResponse> {
    return this.http.get<MfaStatusResponse>(MFA_ENDPOINTS.status);
  }

  enroll(): Observable<MfaEnrollmentResponse> {
    return this.http.post<MfaEnrollmentResponse>(MFA_ENDPOINTS.enroll, {});
  }

  verify(code: string): Observable<MfaEnrollmentConfirmedResponse> {
    const request: MfaCodeRequest = { code };
    return this.http.post<MfaEnrollmentConfirmedResponse>(MFA_ENDPOINTS.verify, request);
  }

  disable(code: string): Observable<void> {
    const request: MfaCodeRequest = { code };
    return this.http.post<void>(MFA_ENDPOINTS.disable, request);
  }
}
