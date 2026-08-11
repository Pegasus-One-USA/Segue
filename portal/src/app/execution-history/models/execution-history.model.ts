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
 *  only ever reflects the success path. */
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

/** Matches the backend's FieldLineageHopDto — one transform node's before/after value for a destination field. */
export interface FieldLineageHop {
  nodeOrder: number;
  nodeType: string;
  configJson: string;
  sourceValueJson: string | null;
  destinationValueJson: string | null;
  success: boolean;
  errorMessage: string | null;
  durationMs: number | null;
}

/** Matches the backend's FieldLineageChainDto — one destination field's full source-to-destination chain for
 *  one resource in this run, ordered by FieldLineageHop.nodeOrder. */
export interface FieldLineageChain {
  resourceType: string;
  resourceId: string;
  destinationField: string;
  sourceField: string | null;
  hops: FieldLineageHop[];
}
