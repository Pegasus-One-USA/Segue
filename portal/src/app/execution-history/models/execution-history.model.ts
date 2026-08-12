export type ExecutionStatus =
  | 'Pending'
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled'
  | 'PartialSuccess'
  | 'AwaitingBulkExport';

/** Matches the backend's WorkflowRunHistoryDto (workflow-runs endpoints — the Runtime Plane's execution history). */
export interface RouteExecution {
  id: string;
  workflowDefinitionId: string;
  pipelineName: string;
  sourceName: string | null;
  sourceSystemType: string | null;
  status: ExecutionStatus;
  startedAt: string;
  completedAt: string | null;
  durationMs: number | null;
  triggeredBy: string | null;
  triggerType: string | null;
  nodeRunCount: number;
  errorMessage: string | null;
  workflowDefinitionVersion: number;
  correlationId: string | null;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface RouteExecutionFilter {
  status?: string;
  source?: string;
  triggeredBy?: string;
  search?: string;
  page: number;
  pageSize: number;
  sortColumn?: string;
  sortDirection?: 'asc' | 'desc';
}

/** Matches the backend's WorkflowRunStatusCountsDto (GET /workflow-runs/stats) — an all-time count per
 *  status across every workflow, backing the Dashboard's status stat tiles. */
export interface WorkflowRunStatusCounts {
  pending: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
}

/** Matches the backend's WorkflowNodeRunPayloadDto — what a single node fetched/transformed/wrote. */
export interface ResourceHistoryEntry {
  id: string;
  workflowNodeRunId: string;
  nodeType: string;
  contract: string;
  payloadJson: string;
  itemCount: number | null;
  recordedAtUtc: string;
}

export type NodeRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';

/** Matches the backend's WorkflowNodeRunHistoryDto — one row per node that actually started this run, with
 *  its real outcome (success, failure, or cancellation) always present, unlike ResourceHistoryEntry which
 *  only ever reflects the success path. payloadJson is always null here — the list never decrypts a node's
 *  output; fetch it on demand via ExecutionHistoryApiService.nodeRunPayload() once a row is expanded. */
export interface NodeRunHistoryEntry {
  workflowNodeRunId: string;
  nodeType: string;
  rank: number;
  subRank: number;
  status: NodeRunStatus;
  errorMessage: string | null;
  startedAt: string;
  completedAt: string | null;
  contract: string | null;
  payloadJson: string | null;
  itemCount: number | null;
}

/** Matches the backend's WorkflowNodeRunPayloadDetailDto — one node run's decrypted output, fetched only when
 *  its row is expanded (see ExecutionHistoryApiService.nodeRunPayload()). */
export interface NodeRunPayloadDetail {
  workflowNodeRunId: string;
  contract: string | null;
  payloadJson: string | null;
  itemCount: number | null;
}

/** Matches the backend's FieldLineageHopDto — one transform node's before/after value for a destination field.
 *  sourceValueJson/destinationValueJson arrive here already decrypted server-side. */
export interface FieldLineageHop {
  nodeOrder: number;
  nodeType: string;
  configJson: string;
  sourceValueJson: string | null;
  destinationValueJson: string | null;
  success: boolean;
  errorMessage: string | null;
  durationMs: number | null;
  executedAtUtc: string;
}

/** Matches the backend's FieldLineageChainDto — one destination field's full source-to-destination chain for
 *  one resource in this run, ordered by FieldLineageHop.nodeOrder. */
export interface FieldLineageChain {
  resourceType: string;
  resourceId: string;
  destinationField: string;
  sourceField: string | null;
  hops: FieldLineageHop[];
  sourceSystemType: string | null;
  sourceConnectionName: string | null;
  destinationTypeName: string | null;
  destinationName: string | null;
}

/** Filters over the field-lineage endpoint — backs the Lineage panel's Group-by-Field/Patient/Node toggle and
 *  free-text search. Matches the backend's FieldLineageFilter (all optional; omit for "no filter"). */
export interface FieldLineageFilter {
  resourceType?: string;
  destinationField?: string;
  resourceId?: string;
  nodeType?: string;
  search?: string;
}

/** Matches the backend's LineageSummaryDto — run-wide field-lineage totals for the Lineage panel's stat strip. */
export interface LineageSummary {
  resourcesProcessed: number;
  fieldsTransformed: number;
  transformationNodesExecuted: number;
  successRate: number;
}

/** Matches the backend's FieldSummaryDto — one destination field's footprint within a resource type. */
export interface FieldSummary {
  destinationField: string;
  resourceCount: number;
}

/** Matches the backend's ResourceTypeSummaryDto — backs the Lineage panel's resource-tree sidebar. */
export interface ResourceTypeSummary {
  resourceType: string;
  resourceCount: number;
  fields: FieldSummary[];
}
