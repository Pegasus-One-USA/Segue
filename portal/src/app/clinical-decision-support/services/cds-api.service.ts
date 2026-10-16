import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CDS_ENDPOINTS } from '../../core/api-endpoints';
import {
  CdsActivityFilter,
  CdsActivityPage,
  CdsCallbackTestResult,
  CdsDiscoveryInfo,
  CdsHandler,
  CdsReadiness,
  CdsRequest,
  CdsServiceInstance,
  CdsSetupSheet,
  CdsSignOffRequest,
  CdsTestResult,
  CdsTrustedIssuer,
  SaveCdsServiceInstanceRequest,
  SaveCdsTrustedIssuerRequest,
} from '../models/cds.models';

/** The CDS Hooks admin API. Every call needs a cdshooks.* permission; errors surface the backend's own message. */
@Injectable({ providedIn: 'root' })
export class CdsApiService {
  private readonly http = inject(HttpClient);

  // ─── Services ────────────────────────────────────────────────────────────────

  getServices(): Observable<CdsServiceInstance[]> {
    return this.http.get<CdsServiceInstance[]>(CDS_ENDPOINTS.services);
  }

  getService(id: string): Observable<CdsServiceInstance> {
    return this.http.get<CdsServiceInstance>(CDS_ENDPOINTS.serviceById(id));
  }

  createService(request: SaveCdsServiceInstanceRequest): Observable<CdsServiceInstance> {
    return this.http.post<CdsServiceInstance>(CDS_ENDPOINTS.services, request);
  }

  updateService(id: string, request: SaveCdsServiceInstanceRequest): Observable<CdsServiceInstance> {
    return this.http.put<CdsServiceInstance>(CDS_ENDPOINTS.serviceById(id), request);
  }

  deleteService(id: string): Observable<void> {
    return this.http.delete<void>(CDS_ENDPOINTS.serviceById(id));
  }

  getSampleRequest(id: string): Observable<CdsRequest> {
    return this.http.get<CdsRequest>(CDS_ENDPOINTS.sampleRequest(id));
  }

  /** Runs the service on a pasted request. Nothing sent here is stored or written to the activity log. */
  testService(id: string, request: CdsRequest): Observable<CdsTestResult> {
    return this.http.post<CdsTestResult>(CDS_ENDPOINTS.test(id), request);
  }

  /** Every value the health system's Epic team must enter by hand for this service. */
  getSetupSheet(id: string): Observable<CdsSetupSheet> {
    return this.http.get<CdsSetupSheet>(CDS_ENDPOINTS.setupSheet(id));
  }

  /** Records a clinician's sign-off for the service as last saved. Returns the updated service. */
  signOffService(id: string, request: CdsSignOffRequest): Observable<CdsServiceInstance> {
    return this.http.post<CdsServiceInstance>(CDS_ENDPOINTS.signOff(id), request);
  }

  /** Removes the clinical sign-off. Returns the updated service. */
  clearSignOff(id: string): Observable<CdsServiceInstance> {
    return this.http.delete<CdsServiceInstance>(CDS_ENDPOINTS.signOff(id));
  }

  // ─── Trusted issuers ─────────────────────────────────────────────────────────

  getTrustedIssuers(): Observable<CdsTrustedIssuer[]> {
    return this.http.get<CdsTrustedIssuer[]>(CDS_ENDPOINTS.trustedIssuers);
  }

  createTrustedIssuer(request: SaveCdsTrustedIssuerRequest): Observable<CdsTrustedIssuer> {
    return this.http.post<CdsTrustedIssuer>(CDS_ENDPOINTS.trustedIssuers, request);
  }

  updateTrustedIssuer(id: string, request: SaveCdsTrustedIssuerRequest): Observable<CdsTrustedIssuer> {
    return this.http.put<CdsTrustedIssuer>(CDS_ENDPOINTS.trustedIssuerById(id), request);
  }

  deleteTrustedIssuer(id: string): Observable<void> {
    return this.http.delete<void>(CDS_ENDPOINTS.trustedIssuerById(id));
  }

  /** Checks the issuer's FHIR server answers (GET /metadata). No token and no patient data are sent. */
  testCallback(id: string): Observable<CdsCallbackTestResult> {
    return this.http.post<CdsCallbackTestResult>(CDS_ENDPOINTS.testCallback(id), null);
  }

  // ─── Catalog ─────────────────────────────────────────────────────────────────

  getHandlers(): Observable<CdsHandler[]> {
    return this.http.get<CdsHandler[]>(CDS_ENDPOINTS.handlers);
  }

  getDiscoveryInfo(): Observable<CdsDiscoveryInfo> {
    return this.http.get<CdsDiscoveryInfo>(CDS_ENDPOINTS.discoveryInfo);
  }

  /** Go-live readiness: every check with ok / warning / blocker and how to fix it. */
  getReadiness(): Observable<CdsReadiness> {
    return this.http.get<CdsReadiness>(CDS_ENDPOINTS.readiness);
  }

  getActivity(filter: CdsActivityFilter): Observable<CdsActivityPage> {
    let params = new HttpParams()
      .set('page', String(filter.page))
      .set('pageSize', String(filter.pageSize));

    if (filter.serviceId) params = params.set('serviceId', filter.serviceId);
    if (filter.kind) params = params.set('kind', filter.kind);
    if (filter.outcome) params = params.set('outcome', filter.outcome);
    if (filter.fromUtc) params = params.set('fromUtc', filter.fromUtc);
    if (filter.toUtc) params = params.set('toUtc', filter.toUtc);

    return this.http.get<CdsActivityPage>(CDS_ENDPOINTS.activity, { params });
  }
}
