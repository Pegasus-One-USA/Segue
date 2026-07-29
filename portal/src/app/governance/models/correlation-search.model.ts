import { AuditLogEntry, AuthenticationLogEntry, AuthorizationLogEntry, DataAccessLogEntry, SecurityEventEntry, SmartLaunchLogEntry } from './governance.model';
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

/** Matches the backend's WorkflowRunSummaryDto — a Runtime-plane (DAG) workflow run header. */
export interface WorkflowRunSummary {
  id: string;
  workflowDefinitionId: string;
  status: string;
  startedAt: string;
  completedAt: string | null;
  triggeredBy: string | null;
  triggerType: string | null;
  errorMessage: string | null;
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
  workflowRuns: WorkflowRunSummary[];
  smartLaunchLogs: SmartLaunchLogEntry[];
  totalCount: number;
}
