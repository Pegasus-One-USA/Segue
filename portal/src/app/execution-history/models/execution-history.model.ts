export type ExecutionStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';

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
