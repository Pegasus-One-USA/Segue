import { AuditLogEntry, AuthenticationLogEntry, AuthorizationLogEntry, DataAccessLogEntry, SecurityEventEntry } from './governance.model';
import {
  ApiRequestLogEntry,
  ErrorLogEntry,
  ExportHistoryEntry,
  NotificationHistoryEntry,
  RetryHistoryEntry,
  SchedulerHistoryEntry,
  ValidationFailureEntry,
} from '../../operations/models/operations.model';

/** Minimal projection of the backend's ConfiguredPipelineRunDto — the Configured Pipeline run header record. */
export interface PipelineRunSummary {
  id: string;
  status: string;
  startedOnUtc: string;
  completedOnUtc: string;
  extractedResourceCount: number;
  mappedRecordCount: number;
  writtenRecordCount: number;
  triggeredBy: string | null;
  triggerType: string | null;
}

/** Matches the backend's CorrelationSearchResultDto (api/v1/governance/correlation-search). */
export interface CorrelationSearchResult {
  correlationId: string;
  pipelineRun: PipelineRunSummary | null;
  auditLogs: AuditLogEntry[];
  dataAccessLogs: DataAccessLogEntry[];
  authenticationLogs: AuthenticationLogEntry[];
  securityEvents: SecurityEventEntry[];
  authorizationLogs: AuthorizationLogEntry[];
  schedulerHistory: SchedulerHistoryEntry[];
  retryHistory: RetryHistoryEntry[];
  errors: ErrorLogEntry[];
  apiRequests: ApiRequestLogEntry[];
  exports: ExportHistoryEntry[];
  notifications: NotificationHistoryEntry[];
  validationFailures: ValidationFailureEntry[];
  totalCount: number;
}
