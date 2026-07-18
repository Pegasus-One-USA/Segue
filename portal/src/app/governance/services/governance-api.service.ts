import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { GOVERNANCE_ENDPOINTS } from '../../core/api-endpoints';
import {
  AlertHistoryEntry,
  AlertRule,
  ArchiveManifestEntry,
  AuditLogEntry,
  AuthenticationLogEntry,
  AuthorizationLogEntry,
  CreateAlertRuleRequest,
  DataAccessLogEntry,
  DataLineage,
  LineageFieldValue,
  LogSettings,
  RetentionPolicyEntry,
  SecurityEventEntry,
  SmartLaunchLogEntry,
} from '../models/governance.model';
import { CorrelationSearchResult } from '../models/correlation-search.model';

function buildParams(correlationId: string | undefined, take: number): HttpParams {
  let params = new HttpParams().set('take', take);
  if (correlationId) {
    params = params.set('correlationId', correlationId);
  }
  return params;
}

@Injectable({ providedIn: 'root' })
export class GovernanceApiService {
  private readonly http = inject(HttpClient);

  auditLogs(correlationId?: string, take = 200, entityType?: string, entityId?: string): Observable<AuditLogEntry[]> {
    let params = buildParams(correlationId, take);
    if (entityType) params = params.set('entityType', entityType);
    if (entityId) params = params.set('entityId', entityId);
    return this.http.get<AuditLogEntry[]>(GOVERNANCE_ENDPOINTS.auditLogs, { params });
  }

  authenticationLogs(correlationId?: string, take = 200, authenticationType?: string): Observable<AuthenticationLogEntry[]> {
    let params = buildParams(correlationId, take);
    if (authenticationType) params = params.set('authenticationType', authenticationType);
    return this.http.get<AuthenticationLogEntry[]>(GOVERNANCE_ENDPOINTS.authenticationLogs, { params });
  }

  dataAccessLogs(correlationId?: string, take = 200): Observable<DataAccessLogEntry[]> {
    return this.http.get<DataAccessLogEntry[]>(GOVERNANCE_ENDPOINTS.dataAccessLogs, { params: buildParams(correlationId, take) });
  }

  securityEvents(correlationId?: string, take = 200): Observable<SecurityEventEntry[]> {
    return this.http.get<SecurityEventEntry[]>(GOVERNANCE_ENDPOINTS.securityEvents, { params: buildParams(correlationId, take) });
  }

  authorizationLogs(correlationId?: string, take = 200): Observable<AuthorizationLogEntry[]> {
    return this.http.get<AuthorizationLogEntry[]>(GOVERNANCE_ENDPOINTS.authorizationLogs, { params: buildParams(correlationId, take) });
  }

  smartLaunchLogs(take = 200): Observable<SmartLaunchLogEntry[]> {
    return this.http.get<SmartLaunchLogEntry[]>(GOVERNANCE_ENDPOINTS.smartLaunchLogs, { params: new HttpParams().set('take', take) });
  }

  retentionPolicies(): Observable<RetentionPolicyEntry[]> {
    return this.http.get<RetentionPolicyEntry[]>(GOVERNANCE_ENDPOINTS.retentionPolicies);
  }

  logSettings(): Observable<LogSettings> {
    return this.http.get<LogSettings>(GOVERNANCE_ENDPOINTS.logSettings);
  }

  archives(): Observable<ArchiveManifestEntry[]> {
    return this.http.get<ArchiveManifestEntry[]>(GOVERNANCE_ENDPOINTS.archives);
  }

  restoreArchive(dataClass: string): Observable<unknown> {
    return this.http.post(GOVERNANCE_ENDPOINTS.restoreArchive(dataClass), {});
  }

  dataLineage(resourceRecordId: string): Observable<DataLineage> {
    return this.http.get<DataLineage>(GOVERNANCE_ENDPOINTS.dataLineage(resourceRecordId));
  }

  revealLineageField(resourceRecordId: string, targetField: string): Observable<LineageFieldValue> {
    return this.http.get<LineageFieldValue>(GOVERNANCE_ENDPOINTS.revealLineageField(resourceRecordId, targetField));
  }

  alertRules(): Observable<AlertRule[]> {
    return this.http.get<AlertRule[]>(GOVERNANCE_ENDPOINTS.alertRules);
  }

  createAlertRule(request: CreateAlertRuleRequest): Observable<AlertRule> {
    return this.http.post<AlertRule>(GOVERNANCE_ENDPOINTS.alertRules, request);
  }

  setAlertRuleEnabled(id: string, isEnabled: boolean): Observable<unknown> {
    return this.http.post(GOVERNANCE_ENDPOINTS.setAlertRuleEnabled(id, isEnabled), {});
  }

  alertHistory(take = 200): Observable<AlertHistoryEntry[]> {
    return this.http.get<AlertHistoryEntry[]>(GOVERNANCE_ENDPOINTS.alerts, { params: new HttpParams().set('take', take) });
  }

  acknowledgeAlert(id: string): Observable<unknown> {
    return this.http.post(GOVERNANCE_ENDPOINTS.acknowledgeAlert(id), {});
  }

  correlationSearch(correlationId: string): Observable<CorrelationSearchResult> {
    const params = new HttpParams().set('correlationId', correlationId);
    return this.http.get<CorrelationSearchResult>(GOVERNANCE_ENDPOINTS.correlationSearch, { params });
  }

  hipaaAuditReport(fromDate: string, toDate: string): Observable<Blob> {
    const params = new HttpParams().set('from', fromDate).set('to', toDate);
    return this.http.get(GOVERNANCE_ENDPOINTS.hipaaAuditReport, { params, responseType: 'blob' });
  }

  soc2EvidenceReport(fromDate: string, toDate: string): Observable<Blob> {
    const params = new HttpParams().set('from', fromDate).set('to', toDate);
    return this.http.get(GOVERNANCE_ENDPOINTS.soc2EvidenceReport, { params, responseType: 'blob' });
  }
}
