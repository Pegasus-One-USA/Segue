/** Matches the backend's SchedulerHistoryDto (api/v1/operations/scheduler-history). */
export interface SchedulerHistoryEntry {
  id: string;
  schedulerId: string;
  runTimeUtc: string;
  status: string;
  routeCount: number;
  correlationId: string | null;
}

/** Matches the backend's RetryHistoryDto (api/v1/operations/retry-history). */
export interface RetryHistoryEntry {
  id: string;
  occurredOnUtc: string;
  context: string;
  retryNumber: number;
  delayMilliseconds: number;
  reason: string;
  correlationId: string | null;
}

/** Matches the backend's ErrorLogDto (api/v1/operations/errors). Phase 6A fields (reference id, category,
 *  execution-correlation set, resolution status) are populated by the Global Exception Manager. */
export interface ErrorLogEntry {
  id: string;
  occurredOnUtc: string;
  severity: string;
  exceptionType: string;
  message: string;
  stackTrace: string | null;
  module: string | null;
  correlationId: string | null;
  errorReferenceId: string | null;
  category: string | null;
  userFriendlyMessage: string | null;
  executionId: string | null;
  workflowId: string | null;
  endpointId: string | null;
  requestId: string | null;
  traceId: string | null;
  spanId: string | null;
  status: string | null;
  resolvedBy: string | null;
  resolvedOnUtc: string | null;
}

/** Multi-criteria filter for the Monitoring → Errors search (Phase 6A). All fields optional. */
export interface ErrorLogSearch {
  errorReferenceId?: string;
  correlationId?: string;
  executionId?: string;
  workflowId?: string;
  endpointId?: string;
  severity?: string;
  category?: string;
  status?: string;
  fromUtc?: string;
  toUtc?: string;
  take?: number;
}

/** The standardized error envelope returned by the backend's Global Exception Manager (Phase 6A). */
export interface StandardErrorResponse {
  error?: string;
  message?: string;
  errorReferenceId?: string;
  correlationId?: string;
  category?: string;
}

/** Matches the backend's ApiRequestLogDto (api/v1/operations/api-requests). */
export interface ApiRequestLogEntry {
  id: string;
  occurredOnUtc: string;
  method: string;
  url: string;
  statusCode: number | null;
  durationMs: number;
  error: string | null;
  correlationId: string | null;
}

/** Matches the backend's ExportHistoryDto (api/v1/operations/exports). */
export interface ExportHistoryEntry {
  id: string;
  occurredOnUtc: string;
  pipelineRunId: string | null;
  destinationName: string;
  format: string;
  rowCount: number;
  fileSizeBytes: number | null;
  status: string;
  correlationId: string | null;
}

/** Matches the backend's NotificationHistoryDto (api/v1/operations/notifications). */
export interface NotificationHistoryEntry {
  id: string;
  occurredOnUtc: string;
  notificationType: string;
  recipient: string;
  subject: string | null;
  status: string;
  error: string | null;
  correlationId: string | null;
}

/** Matches the backend's ValidationFailureDto (api/v1/operations/validation-failures). */
export interface ValidationFailureEntry {
  id: string;
  occurredOnUtc: string;
  resourceType: string;
  resourceId: string | null;
  warningsJson: string;
  dataQualityScore: number | null;
  pipelineRunId: string | null;
  correlationId: string | null;
}

/** Matches the backend's EndpointHealthCheckDto (api/v1/operations/endpoint-health). */
export interface EndpointHealthCheckEntry {
  id: string;
  occurredOnUtc: string;
  endpointName: string;
  endpointType: string;
  status: string;
  latencyMs: number;
  message: string | null;
}

/** Matches the backend's QueueDepthDto (api/v1/operations/queue-monitor). unavailableReason set (not counts
 *  faked) when the transport can't be queried — e.g. Messaging:Provider=InMemory or the admin API is unreachable. */
export interface QueueDepthEntry {
  queueName: string;
  transportType: string;
  pending: number;
  processing: number;
  deadLetter: number;
  lastMessageUtc: string | null;
  unavailableReason: string | null;
}

/** Matches the backend's ApiEndpointStatDto/ApiAnalyticsDto (api/v1/operations/api-analytics) — an in-process
 *  snapshot for this host process only (see IApiMetricsSnapshotProvider's remarks). */
export interface ApiEndpointStat {
  method: string;
  url: string;
  callCount: number;
  averageDurationMs: number;
  p95DurationMs: number;
  errorCount: number;
}

export interface ApiAnalytics {
  totalRequests: number;
  totalErrors: number;
  errorRatePercent: number;
  topByCallCount: ApiEndpointStat[];
  slowestByAverageDuration: ApiEndpointStat[];
}

/** Matches the backend's ComponentHealthDto/SystemHealthDto (api/v1/operations/system-health) — only
 *  components genuinely observable from this host; no placeholder row for a remote Worker process. */
export interface ComponentHealth {
  component: string;
  status: string;
  cpuPercent: number | null;
  memoryBytes: number | null;
  notes: string | null;
}

export interface SystemHealth {
  components: ComponentHealth[];
}

/** Matches the backend's SchedulerSummaryDto (api/v1/operations/scheduler-summary) — real per-route next/last
 *  run, distinct from SchedulerHistoryEntry's per-dispatch-event log. */
export interface SchedulerSummaryEntry {
  routeId: string;
  routeName: string;
  scheduleExpression: string | null;
  nextRunUtc: string | null;
  lastRunStartedUtc: string | null;
  lastRunDurationMs: number | null;
  lastRunStatus: string | null;
}
