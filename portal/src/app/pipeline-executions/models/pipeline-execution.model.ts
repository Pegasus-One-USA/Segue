/** Matches the backend's PipelineRunRouteExecutionDto (api/v1/pipeline-runs/route-executions) — the Configured
 *  Pipeline plane's per-route execution grain (Route-2291-style scheduler/webhook-triggered runs), distinct
 *  from the Runtime DAG plane's execution-history screen. */
export interface PipelineExecutionEntry {
  id: string;
  pipelineRunId: string;
  pipelineName: string;
  sourceName: string;
  sourceSystemType: string;
  status: string;
  startedOnUtc: string;
  completedOnUtc: string | null;
  triggeredBy: string | null;
  triggerType: string | null;
  extractedCount: number;
  mappedCount: number;
  writtenCount: number;
  errorMessage: string | null;
  correlationId: string | null;
  errorCount: number;
  durationMs: number | null;
  /** The Global Exception Manager's ERR-yyyyMMdd-NNNNNN id for this route's failure, when one was actually
   *  persisted to ErrorLogs — null for a non-failed completion, or when no capture ran/persisted (never a
   *  placeholder). Link straight to /operations/errors?errorReferenceId=... */
  errorReferenceId: string | null;
}

export interface PipelineExecutionPagedResult {
  items: PipelineExecutionEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Matches the backend's PipelineRunResourceHistoryDto — per-FHIR-resource-type drill-down. PHI-free: the raw
 *  fetched/normalized/mapped JSON payloads are intentionally not returned. To see an individual decrypted field
 *  value, use the gated + audited reveal on the Data Lineage screen. */
export interface PipelineResourceHistoryEntry {
  id: string;
  routeExecutionId: string;
  resourceType: string;
  sourceResourceId: string | null;
  stage: string;
  errorMessage: string | null;
  fetchedAtUtc: string;
  appliedProfiles: string[];
  warnings: string[];
  dataQualityScore: number | null;
  masterPatientId: string | null;
  normalizedAtUtc: string | null;
  mappedAtUtc: string | null;
  storedAtUtc: string | null;
  writeStatus: string | null;
}

export interface PipelineResourceHistoryPagedResult {
  items: PipelineResourceHistoryEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
}
