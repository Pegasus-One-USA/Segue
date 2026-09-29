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

/**
 * Matches the backend's DestinationActivityLogDto — one stage of one destination write.
 *
 * Complements `apiRequests`, which can only ever show destinations that speak HTTP (the backend produces it from
 * an HttpClient handler); most destinations use ADO.NET or a vendor SDK, so this is the only place they appear.
 */
export interface DestinationActivityEntry {
  id: string;
  occurredOnUtc: string;
  destinationId: string;
  destinationName: string;
  destinationType: string;
  /** "Connect" or "Complete". */
  stage: string;
  /** "Succeeded" | "Failed" | "PartialSuccess" | "NoData". */
  status: string;
  resourceType: string | null;
  recordCount: number | null;
  writtenCount: number | null;
  durationMs: number;
  /** Short technical context, e.g. which half of a two-part Fabric connect. Never record data. */
  detail: string | null;
  error: string | null;
  correlationId: string | null;
  pipelineRunId: string | null;
  /** Plain-language description derived server-side at read time. */
  step: string;
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
  destinationActivity: DestinationActivityEntry[];
  totalCount: number;
}
