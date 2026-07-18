/** Matches the backend's AuditLogDto (GovernanceController — api/v1/governance/audit-logs). */
export interface AuditLogEntry {
  id: string;
  occurredOnUtc: string;
  actor: string;
  module: string;
  action: string;
  entityType: string | null;
  entityId: string | null;
  entityName: string | null;
  oldValueJson: string | null;
  newValueJson: string | null;
  status: string;
  ipAddress: string | null;
  correlationId: string | null;
}

/** Matches the backend's DataAccessLogDto (api/v1/governance/data-access-logs). */
export interface DataAccessLogEntry {
  id: string;
  occurredOnUtc: string;
  actor: string;
  resourceType: string;
  resourceId: string | null;
  action: string;
  patientId: string | null;
  purpose: string | null;
  pipelineRunId: string | null;
  correlationId: string | null;
}

/** Matches the backend's AuthenticationLogDto (api/v1/governance/authentication-logs). */
export interface AuthenticationLogEntry {
  id: string;
  occurredOnUtc: string;
  userEmail: string | null;
  authenticationType: string;
  success: boolean;
  failureReason: string | null;
  ipAddress: string | null;
  correlationId: string | null;
}

/** Matches the backend's AuthorizationLogDto (api/v1/governance/authorization-logs). */
export interface AuthorizationLogEntry {
  id: string;
  occurredOnUtc: string;
  userEmail: string | null;
  requestPath: string;
  permissionCode: string;
  result: string;
  ipAddress: string | null;
  correlationId: string | null;
}

/** Matches the backend's RetentionPolicyDto (api/v1/governance/retention-policies). */
export interface RetentionPolicyEntry {
  dataClass: string;
  retentionYears: number;
  purgeable: boolean;
}

/** Matches the backend's ArchiveManifestDto (api/v1/governance/archives). */
export interface ArchiveManifestEntry {
  dataClass: string;
  archivedThroughUtc: string;
  fileLocation: string;
  recordCount: number;
  createdOnUtc: string;
}

/** Matches the backend's LogCategorySettingDto/LogSettingsDto (api/v1/governance/log-settings). */
export interface LogCategorySetting {
  category: string;
  writesTo: string;
  description: string;
}

export interface LogSettings {
  phiMaskingEnabled: boolean;
  payloadLoggingImplemented: boolean;
  categories: LogCategorySetting[];
}

/** Matches the backend's DataLineageFieldDto/DataLineageDto (api/v1/governance/data-lineage/{id}) — structure
 *  only, no values (see the backend's IDataLineageService remarks). */
export interface DataLineageField {
  sourceField: string;
  mappingRule: string | null;
  destinationColumn: string;
}

export interface DataLineageExport {
  destinationName: string;
  format: string;
  status: string;
  occurredOnUtc: string;
}

export interface DataLineage {
  resourceRecordId: string;
  resourceType: string;
  sourceResourceId: string | null;
  mappingProfileName: string;
  fields: DataLineageField[];
  exports: DataLineageExport[];
}

/** Matches the backend's LineageFieldValueDto — the gated, audited reveal-one-value result. */
export interface LineageFieldValue {
  targetField: string;
  value: string | null;
}

/** Matches the backend's AlertRuleDto/CreateAlertRuleRequest (api/v1/governance/alert-rules). */
export interface AlertRule {
  id: string;
  name: string;
  eventTypeFilter: string;
  thresholdCount: number;
  windowMinutes: number;
  severity: string;
  recipients: string;
  isEnabled: boolean;
}

export interface CreateAlertRuleRequest {
  name: string;
  eventTypeFilter: string;
  thresholdCount: number;
  windowMinutes: number;
  severity: string;
  recipients: string;
}

/** Matches the backend's AlertHistoryDto (api/v1/governance/alerts). */
export interface AlertHistoryEntry {
  id: string;
  alertRuleId: string;
  ruleName: string;
  severity: string;
  summary: string;
  firedOnUtc: string;
  acknowledged: boolean;
  acknowledgedOnUtc: string | null;
  acknowledgedBy: string | null;
}

/** Matches the backend's SmartLaunchLogDto (api/v1/governance/smart-launch-logs). */
export interface SmartLaunchLogEntry {
  id: string;
  occurredOnUtc: string;
  sourceConnectionId: string;
  sourceName: string;
  launchType: string;
  success: boolean;
  failureReason: string | null;
}

/** Matches the backend's SecurityEventDto (api/v1/governance/security-events). */
export interface SecurityEventEntry {
  id: string;
  occurredOnUtc: string;
  severity: string;
  eventType: string;
  userEmail: string | null;
  ipAddress: string | null;
  details: string | null;
  resolved: boolean;
  correlationId: string | null;
}
