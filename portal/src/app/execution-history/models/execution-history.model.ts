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
  /** The Global Exception Manager's ERR-yyyyMMdd-NNNNNN id for this run's failure, when one was actually
   *  persisted to ErrorLogs — null if capture never ran or failed to persist (never a placeholder). Link
   *  straight to /operations/errors?errorReferenceId=... rather than asking the user to search by execution id. */
  errorReferenceId: string | null;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Matches the backend's WorkflowRunHistoryPageDto — a page of runs plus the source systems that actually appear
 *  in the history, so the Source filter lists only vendors with real runs behind them (same facet contract the
 *  Workflows list already uses). */
export interface RouteExecutionPage extends PagedResult<RouteExecution> {
  availableSourceSystemTypes: string[];
}

export interface RouteExecutionFilter {
  /** Set when the Workflows list's "Execution History" row action deep-links here — narrows to one workflow's
   *  runs, exact match on WorkflowDefinitionId (see ExecutionHistoryListComponent's workflowIdFilter). */
  workflowId?: string;
  status?: string;
  source?: string;
  /** Multi-select Source filter — sent as repeated `sources` params. `source` above stays for single-value
   *  callers (Dashboard links). */
  sources?: string[];
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
  partialSuccess: number;
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

/** Shape of a destination node's stored delivery SUMMARY (PascalCase — written by
 *  EfWorkflowNodeResourceHistoryRecorder.SummarizeDelivery), parsed client-side from
 *  NodeRunPayloadDetail.deliveryDetailJson.
 *
 *  Counts and status only, deliberately. The email Subject/Body, the To/Cc addresses, the attachment file names
 *  and the signed download link are no longer persisted — they carry patient identifiers, and storing them in a
 *  table this screen reads is exactly what the PHI removal exists to stop. What an operator needs from this card
 *  is whether the delivery happened and how big it was, which is what remains. */
export interface DestinationWriteResultPayload {
  DestinationId: string | null;
  RecordsWritten: number | null;
  WrittenAt: string | null;
  /** Whether a download was produced. The URL itself is not stored. */
  HasDownload: boolean;
  EmailDelivery: EmailDeliveryDetail | null;
}

export interface EmailDeliveryDetail {
  Status: 'Sent' | 'Failed' | 'Skipped' | string;
  Error: string | null;
  ToCount: number;
  CcCount: number;
  AttachmentCount: number;
}

export type NodeRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';

/** Matches the backend's WorkflowNodeRunHistoryDto — one row per node that actually started this run, with
 *  its real outcome (success, failure, or cancellation) always present, unlike ResourceHistoryEntry which
 *  only ever reflects the success path. Metadata only: node output is no longer retained anywhere, so a row
 *  reports HOW MUCH a node produced (itemCount, resourceTypeCountsJson), never the data itself. */
export interface NodeRunHistoryEntry {
  workflowNodeRunId: string;
  /** The definition node this run executed — field lineage is recorded against this, not the run id. */
  workflowNodeId: string;
  nodeType: string;
  rank: number;
  subRank: number;
  status: NodeRunStatus;
  errorMessage: string | null;
  startedAt: string;
  completedAt: string | null;
  contract: string | null;
  itemCount: number | null;
  /** Destination delivery metadata as JSON (records written, download URL, email envelope). Null otherwise. */
  deliveryDetailJson: string | null;
  /** Per-resource-type counts as JSON, e.g. {"Patient":1,"Observation":42} — type names and totals only. */
  resourceTypeCountsJson: string | null;
}

/** Matches the backend's WorkflowNodeRunPayloadDetailDto — one node run's output SUMMARY, fetched when its
 *  row is expanded (see ExecutionHistoryApiService.nodeRunPayload()). Counts only; output is not retained. */
export interface NodeRunPayloadDetail {
  workflowNodeRunId: string;
  contract: string | null;
  itemCount: number | null;
  resourceTypeCountsJson: string | null;
  deliveryDetailJson: string | null;
}

/** Matches the backend's FieldLineageHopDto — one transform node applied to a destination field. Describes the
 *  TRANSFORMATION (which node, what config, did it succeed, how long) — the field's before/after values are no
 *  longer captured, since those are raw patient data. */
export interface FieldLineageHop {
  nodeOrder: number;
  nodeType: string;
  configJson: string;
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

/** Matches the backend's LineageRuleCountDto — one rule type a node applied, and how many times. */
export interface LineageRuleCount {
  nodeType: string;
  applications: number;
  failedApplications: number;
}

/** Matches the backend's ConfiguredRuleCountDto — one rule type and how many of it the workflow defines. */
export interface ConfiguredRuleCount {
  nodeType: string;
  rulesDefined: number;
}

/** Matches the backend's ConfiguredResourceTypeRulesDto — the transformation rules CONFIGURED for one
 *  resource type on this run's workflow. Distinct from what executed: a rule defined but never triggered
 *  still appears, which is the point of showing configuration rather than runtime lineage. */
export interface ConfiguredResourceTypeRules {
  resourceType: string;
  distinctRuleTypes: number;
  rules: ConfiguredRuleCount[];
}

/** Matches the backend's LineageResourceTypeCountDto — one resource type's share of a node's work. */
export interface LineageResourceTypeCount {
  resourceType: string;
  mappings: number;
  resources: number;
  fields: number;
  /** Transformation rules applied to THIS resource type. Often empty — rules are configured per resource
   *  type, so a type that is only copied field-for-field genuinely has none. */
  rules: LineageRuleCount[];
}

/** Matches the backend's NodeLineageBreakdownDto — what one node actually applied to the data. Nodes that
 *  record no field-level work (source fetches, whole-resource normalization) are absent from the response. */
export interface NodeLineageBreakdown {
  workflowNodeId: string;
  totalApplications: number;
  distinctFields: number;
  distinctResources: number;
  rules: LineageRuleCount[];
  resourceTypes: LineageResourceTypeCount[];
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
